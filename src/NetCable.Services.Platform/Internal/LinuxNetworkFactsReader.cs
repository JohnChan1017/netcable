using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Linux 网络信息读取器：
/// <list type="bullet">
///   <item><c>ip -o addr show</c>：IPv4/IPv6 地址与前缀；</item>
///   <item><c>ip -o link show</c>：MAC 与 UP 标志；</item>
///   <item><c>ip route show default</c>：默认网关（无 ip 时回退 <c>route -n</c>）；</item>
///   <item><c>/etc/resolv.conf</c> / <c>resolvectl dns</c>：DNS；</item>
///   <item>.NET 网络 API：补齐速率、状态、接口类型。</item>
/// </list>
/// 命令缺失或无权限时降级到 .NET API，绝不抛异常。
/// </summary>
internal static class LinuxNetworkFactsReader
{
    /// <summary>读取 Linux 的网络接口、默认网关与 DNS。</summary>
    public static async Task<NetworkFacts> ReadAsync(CancellationToken cancellationToken)
    {
        var bclFacts = BclNetworkReader.ReadFacts();

        var addressText = await CaptureAsync("ip", new[] { "-o", "addr", "show" }, 8000, cancellationToken)
            .ConfigureAwait(false);
        var linkText = await CaptureAsync("ip", new[] { "-o", "link", "show" }, 8000, cancellationToken)
            .ConfigureAwait(false);
        var routeText = await CaptureAsync("ip", new[] { "route", "show", "default" }, 6000, cancellationToken)
            .ConfigureAwait(false);

        var builders = new Dictionary<string, AdapterBuilder>(StringComparer.OrdinalIgnoreCase);
        ParseAddresses(addressText, builders);
        ParseLinks(linkText, builders);

        var interfaces = MergeWithBcl(builders.Values.ToList(), bclFacts.Interfaces);
        if (interfaces.Count == 0) interfaces = bclFacts.Interfaces;
        if (interfaces.Count == 0) return bclFacts;

        var route = ParseDefaultRoute(routeText);
        if (route is null)
        {
            var routeNText = await CaptureAsync("route", new[] { "-n" }, 6000, cancellationToken)
                .ConfigureAwait(false);
            route = ParseRouteN(routeNText);
        }

        var dnsServers = await ReadDnsServersAsync(cancellationToken).ConfigureAwait(false);
        if (dnsServers.Count == 0) dnsServers = bclFacts.DnsServers.ToList();

        var gateway = route?.Gateway ?? bclFacts.DefaultGateway;
        return NetworkFactsComposer.Compose(interfaces, gateway, dnsServers, route?.InterfaceName);
    }

    private static async Task<string?> CaptureAsync(
        string command,
        string[] arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Commanding.CaptureAsync(command, arguments, timeoutMs, cancellationToken).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ 接口解析

    private sealed class AdapterBuilder
    {
        public required string Name { get; init; }
        public string? Mac { get; set; }
        public string? SubnetMask { get; set; }
        public bool IsUpFromFlags { get; set; }
        public List<string> IPv4 { get; } = new();
        public List<string> IPv6 { get; } = new();
    }

    private static void ParseAddresses(string? text, Dictionary<string, AdapterBuilder> builders)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) continue;

            // 形如：2: enp3s0    inet 192.168.1.50/24 brd ...
            var name = tokens[1].TrimEnd(':');
            var atIndex = name.IndexOf('@');
            if (atIndex > 0) name = name[..atIndex];
            if (IsIgnoredInterface(name)) continue;

            for (var i = 2; i < tokens.Length - 1; i++)
            {
                if (tokens[i] is not ("inet" or "inet6")) continue;

                var addressToken = tokens[i + 1];
                var slash = addressToken.LastIndexOf('/');
                var addressPart = slash > 0 ? addressToken[..slash] : addressToken;
                var prefixPart = slash > 0 ? addressToken[(slash + 1)..] : null;

                if (!IPAddress.TryParse(addressPart, out var address)) continue;
                if (IPAddress.IsLoopback(address)) continue;

                var builder = GetOrAdd(builders, name);
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var value = address.ToString();
                    if (!builder.IPv4.Contains(value)) builder.IPv4.Add(value);
                    if (builder.SubnetMask is null &&
                        int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
                    {
                        builder.SubnetMask = PlatformText.PrefixToMask(prefix);
                    }
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var value = address.ToString();
                    if (!builder.IPv6.Contains(value)) builder.IPv6.Add(value);
                }
            }
        }
    }

    private static void ParseLinks(string? text, Dictionary<string, AdapterBuilder> builders)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) continue;

            // 形如：2: enp3s0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 ... link/ether aa:bb:...
            var name = tokens[1].TrimEnd(':');
            var atIndex = name.IndexOf('@');
            if (atIndex > 0) name = name[..atIndex];
            if (IsIgnoredInterface(name)) continue;

            var builder = GetOrAdd(builders, name);
            var flags = tokens[2];
            if (flags.StartsWith('<') && flags.EndsWith('>'))
            {
                builder.IsUpFromFlags = flags.Contains("UP", StringComparison.Ordinal);
            }

            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (tokens[i] is not ("link/ether" or "link/ieee802.11")) continue;
                builder.Mac ??= PlatformText.NormalizeMac(tokens[i + 1]);
            }
        }
    }

    private static AdapterBuilder GetOrAdd(Dictionary<string, AdapterBuilder> builders, string name)
    {
        if (!builders.TryGetValue(name, out var builder))
        {
            builder = new AdapterBuilder { Name = name };
            builders[name] = builder;
        }

        return builder;
    }

    private static bool IsIgnoredInterface(string name) =>
        name is "lo" or "lo0" || name.StartsWith("lo:", StringComparison.Ordinal);

    private static IReadOnlyList<NetworkInterfaceInfo> MergeWithBcl(
        List<AdapterBuilder> builders,
        IReadOnlyList<NetworkInterfaceInfo> bclInterfaces)
    {
        var result = new List<NetworkInterfaceInfo>();
        var matched = new HashSet<NetworkInterfaceInfo>();

        foreach (var builder in builders)
        {
            var match = bclInterfaces.FirstOrDefault(i =>
                            string.Equals(i.Name, builder.Name, StringComparison.OrdinalIgnoreCase))
                        ?? bclInterfaces.FirstOrDefault(i =>
                            i.Description is not null &&
                            i.Description.Contains(builder.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) matched.Add(match);

            result.Add(new NetworkInterfaceInfo(
                builder.Name,
                match?.Description,
                builder.Mac ?? match?.MacAddress,
                builder.IPv4.Count > 0 ? builder.IPv4 : (match?.IPv4Addresses ?? Array.Empty<string>()),
                builder.IPv6.Count > 0 ? builder.IPv6 : (match?.IPv6Addresses ?? Array.Empty<string>()),
                builder.SubnetMask ?? match?.SubnetMask,
                match?.IsUp ?? builder.IsUpFromFlags,
                match?.SpeedBitsPerSecond,
                match?.InterfaceType ?? InferInterfaceType(builder.Name)));
        }

        foreach (var bcl in bclInterfaces)
        {
            if (!matched.Contains(bcl)) result.Add(bcl);
        }

        return result;
    }

    private static string InferInterfaceType(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("wl", StringComparison.Ordinal) ||
            lower.StartsWith("ww", StringComparison.Ordinal) ||
            lower.Contains("wifi", StringComparison.Ordinal))
        {
            return "Wireless80211";
        }

        if (lower.StartsWith("tun", StringComparison.Ordinal) ||
            lower.StartsWith("tap", StringComparison.Ordinal) ||
            lower.StartsWith("wg", StringComparison.Ordinal) ||
            lower.StartsWith("ipsec", StringComparison.Ordinal) ||
            lower.StartsWith("ppp", StringComparison.Ordinal))
        {
            return "Tunnel";
        }

        return "Ethernet";
    }

    // ------------------------------------------------------------------ 网关

    private sealed record DefaultRoute(string Gateway, string? InterfaceName, int Metric);

    private static DefaultRoute? ParseDefaultRoute(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        DefaultRoute? best = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !string.Equals(tokens[0], "default", StringComparison.OrdinalIgnoreCase))
                continue;

            string? gateway = null;
            string? device = null;
            var metric = 0;
            for (var i = 1; i < tokens.Length - 1; i++)
            {
                switch (tokens[i])
                {
                    case "via":
                        gateway = tokens[i + 1];
                        break;
                    case "dev":
                        device = tokens[i + 1];
                        break;
                    case "metric":
                        int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out metric);
                        break;
                }
            }

            if (gateway is null || !IPAddress.TryParse(gateway, out _)) continue;
            var candidate = new DefaultRoute(gateway, device, metric);
            if (best is null || candidate.Metric < best.Metric) best = candidate;
        }

        return best;
    }

    private static DefaultRoute? ParseRouteN(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        DefaultRoute? best = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Destination Gateway Genmask Flags Metric Ref Use Iface
            if (tokens.Length < 8) continue;
            if (tokens[0] != "0.0.0.0") continue;
            if (!IPAddress.TryParse(tokens[1], out _)) continue;
            if (tokens[1] == "0.0.0.0") continue;
            int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var metric);
            var candidate = new DefaultRoute(tokens[1], tokens[7], metric);
            if (best is null || candidate.Metric < best.Metric) best = candidate;
        }

        return best;
    }

    // ------------------------------------------------------------------ DNS

    private static async Task<List<string>> ReadDnsServersAsync(CancellationToken cancellationToken)
    {
        var result = ReadResolvConf();
        // systemd-resolved 的 127.0.0.53 只是本地存根，尝试用 resolvectl 取真实上游
        if (result.Count > 0 && result.Any(d => !d.StartsWith("127.", StringComparison.Ordinal))) return result;

        var text = await CaptureAsync("resolvectl", new[] { "dns" }, 5000, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var address in PlatformText.ExtractIpv4List(text))
            {
                if (address.StartsWith("127.", StringComparison.Ordinal)) continue;
                if (!result.Contains(address, StringComparer.OrdinalIgnoreCase)) result.Add(address);
            }
        }

        return result;
    }

    private static List<string> ReadResolvConf()
    {
        var result = new List<string>();
        try
        {
            const string path = "/etc/resolv.conf";
            if (!File.Exists(path)) return result;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var address = PlatformText.CleanIpValue(parts[1]);
                if (address is null) continue;
                if (!result.Contains(address, StringComparer.OrdinalIgnoreCase)) result.Add(address);
            }
        }
        catch
        {
            // 容器/受限环境可能没有 resolv.conf
        }

        return result;
    }
}
