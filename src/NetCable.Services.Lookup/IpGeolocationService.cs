using System.Net;
using System.Text.Json;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Lookup;

/// <summary>
/// IP 归属地查询服务。使用 ip-api.com 的免密钥 HTTP 接口，先解析主机名，
/// 再查询国家/地区/城市/ISP/ASN/经纬度/时区；任何失败都返回空字段结果，不抛异常。
/// </summary>
public sealed class IpGeolocationService : IIpGeolocationService
{
    private const int TimeoutMs = 10000;
    private const string Endpoint = "http://ip-api.com/json/";
    private const string FieldList =
        "status,message,country,countryCode,region,regionName,city,isp,org,as,asname,lat,lon,timezone,query";

    private readonly HttpClient _http;

    /// <summary>使用内置 HttpClient 与默认接口地址。</summary>
    public IpGeolocationService() : this(null) { }

    /// <summary>可选注入 HttpClient（便于测试或替换传输层）。</summary>
    public IpGeolocationService(HttpClient? httpClient)
    {
        _http = httpClient ?? LookupHttp.CreateClient(timeout: Timeout.InfiniteTimeSpan);
    }

    /// <summary>查询 IP 或主机名的归属地。解析失败/超时/接口报错时返回空字段（Ip 仍保留可识别值），不抛异常。</summary>
    public async Task<IpGeoInfo> LookupAsync(string ipOrHost, CancellationToken cancellationToken = default)
    {
        var input = ipOrHost?.Trim() ?? string.Empty;
        if (input.Length == 0) return Empty(string.Empty);

        var ip = IPAddress.TryParse(input, out var parsed) ? parsed.ToString() : await ResolveHostAsync(input, cancellationToken);
        if (ip is null) return Empty(input);

        try
        {
            var url = $"{Endpoint}{Uri.EscapeDataString(ip)}?fields={FieldList}&lang=en";
            using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, TimeoutMs);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Empty(ip);

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return Empty(ip);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!string.Equals(GetString(root, "status"), "success", StringComparison.OrdinalIgnoreCase))
                return Empty(ip);

            return new IpGeoInfo(
                Ip: GetString(root, "query") ?? ip,
                Country: GetString(root, "country"),
                CountryCode: GetString(root, "countryCode"),
                Region: GetString(root, "regionName") ?? GetString(root, "region"),
                City: GetString(root, "city"),
                Isp: GetString(root, "isp"),
                Organization: GetString(root, "org") ?? GetString(root, "asname"),
                Asn: ExtractAsn(GetString(root, "as")),
                Latitude: GetDouble(root, "lat"),
                Longitude: GetDouble(root, "lon"),
                TimeZone: GetString(root, "timezone"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 超时、TLS/HTTP 异常、JSON 异常都统一降级为无数据结果
            return Empty(ip);
        }
    }

    private static async Task<string?> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault();
            return address?.ToString();
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

    private static string? ExtractAsn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        return first.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? first.ToUpperInvariant() : first;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetDouble(out var number) ? number : null;
    }

    private static IpGeoInfo Empty(string ip) => new(
        Ip: ip,
        Country: null,
        CountryCode: null,
        Region: null,
        City: null,
        Isp: null,
        Organization: null,
        Asn: null,
        Latitude: null,
        Longitude: null,
        TimeZone: null);
}
