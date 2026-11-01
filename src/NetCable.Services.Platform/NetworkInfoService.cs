using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Platform;
using NetCable.Services.Platform.Internal;

namespace NetCable.Services.Platform;

/// <summary>
/// 本机网络信息、系统信息与公网 IP 查询服务。
/// 所有方法都是异常安全的：系统命令缺失、解析失败、网络不可达时返回可展示的空值/兜底结果，
/// 只有调用方主动取消（<see cref="OperationCanceledException"/>）会继续向上抛出。
/// </summary>
public sealed class NetworkInfoService : INetworkInfoService
{
    private const int HttpTimeoutSeconds = 8;

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly Regex TextIpRegex = new(
        @"(?<![\d.])((?:\d{1,3}\.){3}\d{1,3})(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 公网 IP 源：按“ip-api → ipify → ipapi.co → ifconfig.me”顺序尝试；
    // 本机 SChannel 损坏导致 HTTPS 失败时会自动降级到 http（最后兜底）。
    private static readonly string[] PublicIpSources =
    {
        "https://ip-api.com/json/?fields=status,message,query",
        "http://ip-api.com/json/?fields=status,message,query",
        "https://api.ipify.org?format=json",
        "http://api.ipify.org?format=json",
        "https://ipapi.co/json/",
        "https://ifconfig.me/ip",
        "http://ifconfig.me/ip"
    };

    private const string IpApiGeoFields =
        "status,message,country,countryCode,regionName,city,isp,org,as,lat,lon,timezone,query";

    private readonly IWifiService? _wifiService;

    /// <summary>创建服务实例（不查询 WiFi）。</summary>
    public NetworkInfoService()
        : this(null)
    {
    }

    /// <summary>创建服务实例，可注入 <see cref="IWifiService"/> 以复用其能力探测结果。</summary>
    public NetworkInfoService(IWifiService? wifiService)
    {
        _wifiService = wifiService;
    }

    /// <summary>读取本机名、默认网关、DNS、全部网卡以及当前主 IP/CIDR。</summary>
    public async Task<NetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var facts = await NetworkFacts.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkInfo(
                SafeMachineName(),
                facts.DefaultGateway,
                facts.DnsServers,
                facts.Interfaces,
                facts.CurrentIp,
                facts.SubnetCidr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return EmptyNetworkInfo();
        }
    }

    /// <summary>读取主机名、OS 描述、架构、.NET 版本、提权状态、用户名与 WiFi 支持情况。</summary>
    public Task<SystemInfoSummary> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var wifiSupported = DetectWirelessAdapter() && (_wifiService?.IsSupported ?? true);
            return Task.FromResult(new SystemInfoSummary(
                SafeMachineName(),
                SafeOsDisplayName(),
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                Environment.Version.ToString(),
                Environment.IsPrivilegedProcess,
                SafeUserName(),
                wifiSupported));
        }
        catch
        {
            return Task.FromResult(new SystemInfoSummary(
                SafeMachineName(),
                PlatformInfo.DisplayName,
                PlatformInfo.Architecture,
                Environment.Version.ToString(),
                false,
                null,
                false));
        }
    }

    /// <summary>查询公网 IP（依次尝试 ip-api、ipify、ipapi.co、ifconfig.me，全部失败返回 null）。</summary>
    public async Task<string?> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        foreach (var source in PublicIpSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var body = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
                if (body is null) continue;
                var ip = ExtractIpFromBody(body);
                if (ip is not null) return ip;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 单个源失败继续尝试下一个
            }
        }

        return null;
    }

    /// <summary>查询公网 IP 的地理/归属信息（ip-api → ipapi.co → ipinfo.io；全部失败时只返回 IP）。</summary>
    public async Task<IpGeoInfo?> GetPublicIpInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var geo = await TryIpApiGeoAsync(cancellationToken).ConfigureAwait(false)
                      ?? await TryIpApiCoGeoAsync(cancellationToken).ConfigureAwait(false)
                      ?? await TryIpInfoGeoAsync(cancellationToken).ConfigureAwait(false);
            if (geo is not null) return geo;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 地理信息失败时退化为只返回 IP
        }

        var ip = await GetPublicIpAsync(cancellationToken).ConfigureAwait(false);
        return ip is null
            ? null
            : new IpGeoInfo(ip, null, null, null, null, null, null, null, null, null, null);
    }

    // ------------------------------------------------------------------ 系统信息

    private static string SafeMachineName()
    {
        try
        {
            var name = Environment.MachineName;
            return string.IsNullOrWhiteSpace(name) ? Dns.GetHostName() : name;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? SafeUserName()
    {
        try
        {
            return string.IsNullOrWhiteSpace(Environment.UserName) ? null : Environment.UserName;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeOsDisplayName()
    {
        try
        {
            var description = RuntimeInformation.OSDescription?.Trim();
            return string.IsNullOrWhiteSpace(description) ? PlatformInfo.DisplayName : description;
        }
        catch
        {
            return PlatformInfo.DisplayName;
        }
    }

    private static bool DetectWirelessAdapter()
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return true;

                var text = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
                if (text.Contains("wi-fi", StringComparison.Ordinal) ||
                    text.Contains("wlan", StringComparison.Ordinal) ||
                    text.Contains("wireless", StringComparison.Ordinal) ||
                    text.Contains("无线", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 忽略探测失败
        }

        return false;
    }

    // ------------------------------------------------------------------ HTTP / JSON

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetCable/1.0 (+https://github.com/netcable)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    private static async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(HttpTimeoutSeconds));
        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractIpFromBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "ip", "query" })
                {
                    if (!document.RootElement.TryGetProperty(propertyName, out var value)) continue;
                    if (value.ValueKind != JsonValueKind.String) continue;
                    var candidate = value.GetString();
                    if (IsUsableAddress(candidate)) return candidate!.Trim();
                }
            }
        }
        catch (JsonException)
        {
            // 部分源返回纯文本 IP，继续走文本解析
        }
        catch
        {
            // 忽略异常并尝试文本解析
        }

        var trimmed = body.Trim();
        if (IsUsableAddress(trimmed)) return trimmed;

        foreach (Match match in TextIpRegex.Matches(body))
        {
            if (IsUsableAddress(match.Groups[1].Value)) return match.Groups[1].Value;
        }

        return null;
    }

    private static bool IsUsableAddress(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!IPAddress.TryParse(candidate.Trim(), out var address)) return false;
        if (IPAddress.IsLoopback(address)) return false;
        return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
    }

    private static async Task<IpGeoInfo?> TryIpApiGeoAsync(CancellationToken cancellationToken)
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            var url = $"{scheme}://ip-api.com/json/?fields={IpApiGeoFields}";
            var body = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
            if (body is null) continue;

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(GetString(root, "status"), "success", StringComparison.OrdinalIgnoreCase)) continue;

                var ip = GetString(root, "query");
                if (string.IsNullOrWhiteSpace(ip)) continue;

                return new IpGeoInfo(
                    ip,
                    GetString(root, "country"),
                    GetString(root, "countryCode"),
                    GetString(root, "regionName"),
                    GetString(root, "city"),
                    GetString(root, "isp"),
                    GetString(root, "org"),
                    GetString(root, "as"),
                    GetDouble(root, "lat"),
                    GetDouble(root, "lon"),
                    GetString(root, "timezone"));
            }
            catch (JsonException)
            {
                // 尝试下一个协议
            }
            catch
            {
                // 尝试下一个协议
            }
        }

        return null;
    }

    private static async Task<IpGeoInfo?> TryIpApiCoGeoAsync(CancellationToken cancellationToken)
    {
        var body = await FetchAsync("https://ipapi.co/json/", cancellationToken).ConfigureAwait(false);
        if (body is null) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var ip = GetString(root, "ip");
            if (string.IsNullOrWhiteSpace(ip)) return null;

            var organization = GetString(root, "org");
            return new IpGeoInfo(
                ip,
                GetString(root, "country_name"),
                GetString(root, "country_code"),
                GetString(root, "region"),
                GetString(root, "city"),
                organization,
                organization,
                GetString(root, "asn"),
                GetDouble(root, "latitude"),
                GetDouble(root, "longitude"),
                GetString(root, "timezone"));
        }
        catch (JsonException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IpGeoInfo?> TryIpInfoGeoAsync(CancellationToken cancellationToken)
    {
        var body = await FetchAsync("https://ipinfo.io/json", cancellationToken).ConfigureAwait(false);
        if (body is null) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var ip = GetString(root, "ip");
            if (string.IsNullOrWhiteSpace(ip)) return null;

            var organization = GetString(root, "org");
            string? asn = null;
            if (!string.IsNullOrWhiteSpace(organization))
            {
                var space = organization.IndexOf(' ');
                asn = space > 0 ? organization[..space] : organization;
            }

            double? latitude = null;
            double? longitude = null;
            var location = GetString(root, "loc");
            if (!string.IsNullOrWhiteSpace(location))
            {
                var parts = location.Split(',', 2);
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    latitude = lat;
                    longitude = lon;
                }
            }

            return new IpGeoInfo(
                ip,
                GetString(root, "country"),
                GetString(root, "country"),
                GetString(root, "region"),
                GetString(root, "city"),
                organization,
                organization,
                asn,
                latitude,
                longitude,
                GetString(root, "timezone"));
        }
        catch (JsonException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var value)) return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static NetworkInfo EmptyNetworkInfo() => new(
        SafeMachineName(),
        null,
        Array.Empty<string>(),
        Array.Empty<NetworkInterfaceInfo>(),
        null,
        null);
}
