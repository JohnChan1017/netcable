using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Windows 网络信息读取器：
/// <list type="bullet">
///   <item><c>ipconfig /all</c>：网卡、MAC、IPv4/IPv6、掩码、DNS（中英文关键字都兼容）；</item>
///   <item><c>route print -4</c>：取度量值最小的默认路由作为默认网关（纯数字列，不受系统语言影响）；</item>
///   <item>.NET 网络 API：补齐速率、链路状态、接口类型。</item>
/// </list>
/// 任一命令缺失/无权限时自动降级到 .NET API，绝不抛异常。
/// </summary>
internal static class WindowsNetworkFactsReader
{
    private static readonly Regex KeyValueRegex = new(
        @"^\s*(?<key>[^:]+?)\s+:\s?(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AdapterHeaderRegex = new(
        @"(?:适配器|adapter)\s+(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DefaultRouteRegex = new(
        @"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(?<gateway>\S+)\s+(?<iface>\S+)\s+(?<metric>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>读取 Windows 的网络接口、默认网关与 DNS。</summary>
    public static async Task<NetworkFacts> ReadAsync(CancellationToken cancellationToken)
    {
        var bclFacts = BclNetworkReader.ReadFacts();

        string? ipconfigText = null;
        string? routeText = null;
        try
        {
            ipconfigText = await Commanding.CaptureAsync("ipconfig", new[] { "/all" }, 12000, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 降级到 .NET API
        }

        try
        {
            routeText = await Commanding.CaptureAsync("route", new[] { "print", "-4" }, 8000, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 默认网关回退到 ipconfig / .NET API
        }

        var adapters = ParseIpconfig(ipconfigText);
        var interfaces = adapters.Count > 0 ? MergeWithBcl(adapters, bclFacts.Interfaces) : bclFacts.Interfaces;
        if (interfaces.Count == 0) return bclFacts;

        var defaultRoute = ParseDefaultRoute(routeText);
        var ipconfigGateway = adapters
            .SelectMany(a => a.Gateways)
            .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        var gateway = defaultRoute?.Gateway ?? ipconfigGateway ?? bclFacts.DefaultGateway;

        string? routeInterfaceName = null;
        if (defaultRoute is not null)
        {
            routeInterfaceName = interfaces
                .FirstOrDefault(i => i.IPv4Addresses.Contains(defaultRoute.InterfaceAddress, StringComparer.OrdinalIgnoreCase))
                ?.Name;
        }

        var dnsServers = adapters.SelectMany(a => a.DnsServers).ToList();
        if (dnsServers.Count == 0) dnsServers = bclFacts.DnsServers.ToList();

        return NetworkFactsComposer.Compose(interfaces, gateway, dnsServers, routeInterfaceName);
    }

    // ------------------------------------------------------------------ ipconfig

    private sealed class Adapter
    {
        public string Name { get; init; } = string.Empty;
        public string? Description { get; set; }
        public string? Mac { get; set; }
        public List<string> IPv4 { get; } = new();
        public List<string> IPv6 { get; } = new();
        public string? SubnetMask { get; set; }
        public List<string> Gateways { get; } = new();
        public List<string> DnsServers { get; } = new();
        public bool MediaDisconnected { get; set; }
    }

    private static List<Adapter> ParseIpconfig(string? text)
    {
        var adapters = new List<Adapter>();
        if (string.IsNullOrWhiteSpace(text)) return adapters;

        Adapter? current = null;
        string? lastKey = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                lastKey = null;
                continue;
            }

            // 适配器标题行：非缩进且带冒号，例如 “Ethernet adapter 以太网:”
            if (!char.IsWhiteSpace(line[0]))
            {
                var colon = line.LastIndexOf(':');
                if (colon > 0)
                {
                    var header = line[..colon].Trim();
                    if (header.Contains("adapter", StringComparison.OrdinalIgnoreCase) ||
                        header.Contains("适配器", StringComparison.Ordinal))
                    {
                        var nameMatch = AdapterHeaderRegex.Match(header);
                        current = new Adapter
                        {
                            Name = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : header
                        };
                        adapters.Add(current);
                        lastKey = null;
                    }
                }

                continue;
            }

            if (current is null) continue;

            var match = KeyValueRegex.Match(line);
            if (!match.Success)
            {
                // 多值续行（DNS 服务器、IPv6 地址等可能换行）
                if (lastKey is not null) ApplyValue(current, lastKey, line.Trim());
                continue;
            }

            lastKey = NormalizeKey(match.Groups["key"].Value);
            ApplyValue(current, lastKey, match.Groups["value"].Value.Trim());
        }

        return adapters;
    }

    private static void ApplyValue(Adapter adapter, string? normalizedKey, string value)
    {
        if (string.IsNullOrEmpty(normalizedKey) || string.IsNullOrWhiteSpace(value)) return;

        if (IsKey(normalizedKey, "description", "描述"))
        {
            adapter.Description ??= value.Trim();
            return;
        }

        if (IsKey(normalizedKey, "physicaladdress", "物理地址"))
        {
            adapter.Mac ??= PlatformText.NormalizeMac(value);
            return;
        }

        if (IsKey(normalizedKey, "subnetmask", "子网掩码"))
        {
            adapter.SubnetMask ??= PlatformText.CleanIpValue(value) ?? PlatformText.ExtractFirstIpv4(value);
            return;
        }

        if (IsKey(normalizedKey, "defaultgateway", "默认网关"))
        {
            foreach (var address in ExtractAddresses(value))
            {
                if (!adapter.Gateways.Contains(address, StringComparer.OrdinalIgnoreCase)) adapter.Gateways.Add(address);
            }

            return;
        }

        if (IsKey(normalizedKey, "dnsservers", "dns服务器"))
        {
            foreach (var address in ExtractAddresses(value))
            {
                if (!adapter.DnsServers.Contains(address, StringComparer.OrdinalIgnoreCase)) adapter.DnsServers.Add(address);
            }

            return;
        }

        if (IsKey(normalizedKey, "mediastate", "媒体状态"))
        {
            if (value.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("断开", StringComparison.Ordinal))
            {
                adapter.MediaDisconnected = true;
            }

            return;
        }

        if (IsIpv4AddressKey(normalizedKey))
        {
            var address = PlatformText.ExtractFirstIpv4(value);
            if (address is not null && !adapter.IPv4.Contains(address)) adapter.IPv4.Add(address);
            return;
        }

        if (IsIpv6AddressKey(normalizedKey))
        {
            var address = PlatformText.CleanIpValue(value);
            if (address is null) return;
            if (IPAddress.TryParse(address, out var parsed) &&
                parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                !IPAddress.IsLoopback(parsed) &&
                !adapter.IPv6.Contains(address))
            {
                adapter.IPv6.Add(address);
            }
        }
    }

    private static IReadOnlyList<string> ExtractAddresses(string value)
    {
        var addresses = new List<string>();
        foreach (var address in PlatformText.ExtractIpv4List(value))
        {
            if (!addresses.Contains(address)) addresses.Add(address);
        }

        if (addresses.Count == 0)
        {
            var single = PlatformText.CleanIpValue(value);
            if (single is not null) addresses.Add(single);
        }

        return addresses;
    }

    /// <summary>去掉空白与点号后的小写关键字，例如 “Physical Address” → “physicaladdress”。</summary>
    private static string NormalizeKey(string key)
    {
        var characters = key
            .Where(c => !char.IsWhiteSpace(c) && c != '.' && c != '\u3000' && c != ':')
            .ToArray();
        return new string(characters).ToLowerInvariant();
    }

    private static bool IsKey(string key, params string[] candidates) =>
        candidates.Any(c => string.Equals(key, c, StringComparison.OrdinalIgnoreCase));

    private static bool IsIpv4AddressKey(string key) =>
        key.Contains("ipv4", StringComparison.Ordinal) &&
        (key.Contains("address", StringComparison.Ordinal) || key.Contains("地址", StringComparison.Ordinal)) &&
        !key.Contains("gateway", StringComparison.Ordinal) &&
        !key.Contains("网关", StringComparison.Ordinal);

    private static bool IsIpv6AddressKey(string key) =>
        key.Contains("ipv6", StringComparison.Ordinal) &&
        (key.Contains("address", StringComparison.Ordinal) || key.Contains("地址", StringComparison.Ordinal)) &&
        !key.Contains("gateway", StringComparison.Ordinal) &&
        !key.Contains("网关", StringComparison.Ordinal);

    // ------------------------------------------------------------------ merge / fallback

    private static IReadOnlyList<NetworkInterfaceInfo> MergeWithBcl(
        List<Adapter> adapters,
        IReadOnlyList<NetworkInterfaceInfo> bclInterfaces)
    {
        var result = new List<NetworkInterfaceInfo>(adapters.Count);
        foreach (var adapter in adapters)
        {
            var match = FindMatch(adapter, bclInterfaces);

            var name = adapter.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\uFFFD'))
            {
                name = match?.Name ?? "网卡";
            }

            var description = string.IsNullOrWhiteSpace(adapter.Description) ? match?.Description : adapter.Description;
            var mac = adapter.Mac ?? match?.MacAddress;
            var ipv4 = adapter.IPv4.Count > 0 ? adapter.IPv4 : (match?.IPv4Addresses ?? Array.Empty<string>());
            var ipv6 = adapter.IPv6.Count > 0 ? adapter.IPv6 : (match?.IPv6Addresses ?? Array.Empty<string>());
            var subnetMask = adapter.SubnetMask ?? match?.SubnetMask;
            var isUp = match?.IsUp ??
                       (!adapter.MediaDisconnected &&
                        (adapter.IPv4.Count > 0 || adapter.IPv6.Count > 0 || adapter.Gateways.Count > 0));
            var interfaceType = match?.InterfaceType ?? InferInterfaceType(name, description);

            result.Add(new NetworkInterfaceInfo(
                name,
                description,
                mac,
                ipv4,
                ipv6,
                subnetMask,
                isUp,
                match?.SpeedBitsPerSecond,
                interfaceType));
        }

        return result;
    }

    private static NetworkInterfaceInfo? FindMatch(Adapter adapter, IReadOnlyList<NetworkInterfaceInfo> bclInterfaces)
    {
        if (!string.IsNullOrWhiteSpace(adapter.Mac))
        {
            var byMac = bclInterfaces.FirstOrDefault(i =>
                string.Equals(i.MacAddress, adapter.Mac, StringComparison.OrdinalIgnoreCase));
            if (byMac is not null) return byMac;
        }

        if (!string.IsNullOrWhiteSpace(adapter.Description))
        {
            var description = adapter.Description;
            var byDescription = bclInterfaces.FirstOrDefault(i =>
                string.Equals(i.Description, description, StringComparison.OrdinalIgnoreCase));
            if (byDescription is not null) return byDescription;

            byDescription = bclInterfaces.FirstOrDefault(i =>
                i.Description is not null &&
                (i.Description.Contains(description, StringComparison.OrdinalIgnoreCase) ||
                 description.Contains(i.Description, StringComparison.OrdinalIgnoreCase)));
            if (byDescription is not null) return byDescription;
        }

        if (!string.IsNullOrWhiteSpace(adapter.Name) && !adapter.Name.Contains('\uFFFD'))
        {
            var byName = bclInterfaces.FirstOrDefault(i =>
                string.Equals(i.Name, adapter.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Description, adapter.Name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        return null;
    }

    private static string InferInterfaceType(string name, string? description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        if (text.Contains("wi-fi") || text.Contains("wifi") || text.Contains("wireless") ||
            text.Contains("wlan") || text.Contains("802.11") || text.Contains("无线"))
        {
            return "Wireless80211";
        }

        if (text.Contains("tunnel") || text.Contains("vpn") || text.Contains("tap") ||
            text.Contains("tun") || text.Contains("wireguard") || text.Contains("meta"))
        {
            return "Tunnel";
        }

        return "Ethernet";
    }

    // ------------------------------------------------------------------ route

    private sealed record DefaultRoute(string Gateway, string InterfaceAddress, int Metric);

    private static DefaultRoute? ParseDefaultRoute(string? routeText)
    {
        if (string.IsNullOrWhiteSpace(routeText)) return null;

        DefaultRoute? best = null;
        foreach (var rawLine in routeText.Split('\n'))
        {
            var match = DefaultRouteRegex.Match(rawLine.TrimEnd('\r'));
            if (!match.Success) continue;
            if (!IPAddress.TryParse(match.Groups["gateway"].Value, out _)) continue;
            if (!int.TryParse(match.Groups["metric"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metric))
                continue;

            var candidate = new DefaultRoute(
                match.Groups["gateway"].Value,
                match.Groups["iface"].Value,
                metric);
            if (best is null || candidate.Metric < best.Metric) best = candidate;
        }

        return best;
    }
}
