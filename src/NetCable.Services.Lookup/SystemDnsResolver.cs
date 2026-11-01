using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NetCable.Core.Platform;

namespace NetCable.Services.Lookup;

/// <summary>
/// 读取系统 DNS 服务器地址，真正区分三个平台：
/// Windows 枚举网卡 DNS 并以 ipconfig /all 兜底；macOS 读取 scutil --dns；
/// Linux 读取 /etc/resolv.conf 并以 resolvectl 兜底；最后统一用网卡信息补齐。
/// </summary>
internal static partial class SystemDnsResolver
{
    [GeneratedRegex(@"nameserver\[\d+\]\s*:\s*(?<ip>[0-9a-fA-F:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MacScutilRegex();

    [GeneratedRegex(@"(?m)^\s*nameserver\s+(?<ip>[0-9a-fA-F:.]*)", RegexOptions.IgnoreCase)]
    private static partial Regex LinuxResolvRegex();

    [GeneratedRegex(@"(?m)^\s*DNS\s+Servers?\s*:\s*(?<ip>[0-9a-fA-F:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolvectlRegex();

    /// <summary>获取系统 DNS 服务器列表（IPv4 优先，已去重）。失败时返回空列表而不是抛异常。</summary>
    public static async Task<IReadOnlyList<string>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var servers = new List<string>();

        try
        {
            var platformServers = PlatformInfo.Family switch
            {
                OsFamily.Windows => await GetWindowsServersAsync(cancellationToken).ConfigureAwait(false),
                OsFamily.MacOS => await GetMacServersAsync(cancellationToken).ConfigureAwait(false),
                OsFamily.Linux => await GetLinuxServersAsync(cancellationToken).ConfigureAwait(false),
                _ => new List<string>()
            };
            AddRange(servers, platformServers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 平台命令不可用时继续用网卡信息兜底
        }

        try
        {
            AddRange(servers, GetInterfaceServers());
        }
        catch
        {
            // 网卡枚举失败时忽略
        }

        // IPv4 优先（部分网络对 UDP/53 的 IPv6 路由不可用）
        return servers
            .OrderBy(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
    }

    private static async Task<List<string>> GetWindowsServersAsync(CancellationToken cancellationToken)
    {
        var servers = GetInterfaceServers();
        if (servers.Count > 0) return servers;

        // 网卡枚举拿不到时解析 ipconfig /all（兼容中英文系统）
        var result = await ProcessRunner.RunAsync("ipconfig", new[] { "/all" }, 5000, cancellationToken)
            .ConfigureAwait(false);
        if (result.StandardOutput.Length == 0) return servers;

        string? pendingLabel = null;
        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { pendingLabel = null; continue; }

            if (line.Contains("DNS", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("Server", StringComparison.OrdinalIgnoreCase) || line.Contains("服务器", StringComparison.Ordinal)))
            {
                var colon = line.LastIndexOf(':');
                if (colon < 0) continue;
                var tail = line[(colon + 1)..];
                if (ExtractIps(tail).Count == 0)
                {
                    pendingLabel = "dns"; // 值在下一行（老版本 ipconfig 输出）
                    continue;
                }
                AddRange(servers, ExtractIps(tail));
                pendingLabel = null;
                continue;
            }

            if (pendingLabel is not null && !line.Contains(':'))
            {
                AddRange(servers, ExtractIps(line));
                continue;
            }

            pendingLabel = null;
        }

        return servers;
    }

    private static async Task<List<string>> GetMacServersAsync(CancellationToken cancellationToken)
    {
        var servers = new List<string>();
        var result = await ProcessRunner.RunAsync("scutil", new[] { "--dns" }, 5000, cancellationToken)
            .ConfigureAwait(false);
        if (result.StandardOutput.Length > 0)
        {
            // 去掉 "DNS configuration" 之后的 "DNS configuration (for scoped queries)" 段可能重复，去重即可
            foreach (Match match in MacScutilRegex().Matches(result.StandardOutput))
                AddUnique(servers, match.Groups["ip"].Value);
        }

        if (servers.Count == 0) AddRange(servers, await ReadResolvConfAsync(cancellationToken).ConfigureAwait(false));
        return servers;
    }

    private static async Task<List<string>> GetLinuxServersAsync(CancellationToken cancellationToken)
    {
        var servers = await ReadResolvConfAsync(cancellationToken).ConfigureAwait(false);
        if (servers.Count > 0) return servers;

        var result = await ProcessRunner.RunAsync("resolvectl", new[] { "status" }, 4000, cancellationToken)
            .ConfigureAwait(false);
        foreach (Match match in ResolvectlRegex().Matches(result.StandardOutput))
            AddUnique(servers, match.Groups["ip"].Value);
        return servers;
    }

    private static async Task<List<string>> ReadResolvConfAsync(CancellationToken cancellationToken)
    {
        var servers = new List<string>();
        const string path = "/etc/resolv.conf";
        try
        {
            if (!File.Exists(path)) return servers;
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (Match match in LinuxResolvRegex().Matches(text))
                AddUnique(servers, match.Groups["ip"].Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 无权限或文件损坏时返回空
        }
        return servers;
    }

    private static List<string> GetInterfaceServers()
    {
        var servers = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                     .ThenByDescending(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                foreach (var address in nic.GetIPProperties().DnsAddresses)
                {
                    if (IsUsable(address)) AddUnique(servers, address.ToString());
                }
            }
            catch
            {
                // 单个网卡读取失败时跳过
            }
        }
        return servers;
    }

    private static bool IsUsable(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)) return false;
        return true;
    }

    private static List<string> ExtractIps(string text)
    {
        var ips = new List<string>();
        foreach (var token in text.Split(new[] { ' ', '\t', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = token.Trim().TrimEnd('.', '。');
            if (IPAddress.TryParse(value, out var address) && IsUsable(address))
                AddUnique(ips, address.ToString());
        }
        return ips;
    }

    private static void AddRange(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values) AddUnique(target, value);
    }

    private static void AddUnique(List<string> target, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var value = raw.Trim();
        if (!IPAddress.TryParse(value, out var address) || !IsUsable(address)) return;
        var normalized = address.ToString();
        if (!target.Contains(normalized, StringComparer.OrdinalIgnoreCase)) target.Add(normalized);
    }
}
