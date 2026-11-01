using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// macOS 网络信息读取器：
/// <list type="bullet">
///   <item><c>ifconfig -a</c>：网卡、MAC、IPv4/IPv6、netmask；</item>
///   <item><c>route -n get default</c>：默认网关与默认路由网卡（回退 <c>netstat -rn</c>）；</item>
///   <item><c>scutil --dns</c>：DNS 服务器；</item>
///   <item>.NET 网络 API：补齐速率、状态、接口类型。</item>
/// </list>
/// 命令缺失或无权限时降级到 .NET API，绝不抛异常。
/// </summary>
internal static class MacNetworkFactsReader
{
    /// <summary>读取 macOS 的网络接口、默认网关与 DNS。</summary>
    public static async Task<NetworkFacts> ReadAsync(CancellationToken cancellationToken)
    {
        var bclFacts = BclNetworkReader.ReadFacts();

        var ifconfigText = await CaptureAsync("ifconfig", new[] { "-a" }, 8000, cancellationToken)
            .ConfigureAwait(false);
        var routeText = await CaptureAsync("route", new[] { "-n", "get", "default" }, 6000, cancellationToken)
            .ConfigureAwait(false);
        var dnsText = await CaptureAsync("scutil", new[] { "--dns" }, 8000, cancellationToken)
            .ConfigureAwait(false);

        var builders = ParseIfconfig(ifconfigText);
        var interfaces = MergeWithBcl(builders, bclFacts.Interfaces);
        if (interfaces.Count == 0) interfaces = bclFacts.Interfaces;
        if (interfaces.Count == 0) return bclFacts;

        var route = ParseDefaultRoute(routeText);
        if (route is null)
        {
            var netstatText = await CaptureAsync("netstat", new[] { "-rn" }, 8000, cancellationToken)
                .ConfigureAwait(false);
            route = ParseNetstatRoutes(netstatText);
        }

        var dnsServers = ParseScutilDns(dnsText);
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

    // ------------------------------------------------------------------ ifconfig

    private sealed class AdapterBuilder
    {
        public required string Name { get; init; }
        public string? Mac { get; set; }
        public string? SubnetMask { get; set; }
        public bool IsUp { get; set; }
        public List<string> IPv4 { get; } = new();
        public List<string> IPv6 { get; } = new();
    }

    private static List<AdapterBuilder> ParseIfconfig(string? text)
    {
        var builders = new List<AdapterBuilder>();
        if (string.IsNullOrWhiteSpace(text)) return builders;

        AdapterBuilder? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            // 接口首行：en0: flags=8863<UP,BROADCAST,...> mtu 1500
            if (!char.IsWhiteSpace(line[0]))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Contains("flags=", StringComparison.Ordinal))
                {
                    var name = line[..colon].Trim();
                    if (IsIgnoredInterface(name))
                    {
                        current = null;
                        continue;
                    }

                    current = new AdapterBuilder { Name = name };
                    var flagsStart = line.IndexOf('<');
                    var flagsEnd = line.IndexOf('>');
                    if (flagsStart >= 0 && flagsEnd > flagsStart)
                    {
                        var flags = line[(flagsStart + 1)..flagsEnd];
                        current.IsUp = flags.Contains("UP", StringComparison.Ordinal);
                    }

                    builders.Add(current);
                }

                continue;
            }

            if (current is null) continue;
            var trimmed = line.Trim();

            if (trimmed.StartsWith("ether ", StringComparison.Ordinal))
            {
                current.Mac ??= PlatformText.NormalizeMac(trimmed[6..]);
                continue;
            }

            if (trimmed.StartsWith("status:", StringComparison.Ordinal))
            {
                current.IsUp = trimmed.Contains("active", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (trimmed.StartsWith("inet ", StringComparison.Ordinal))
            {
                var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // inet 192.168.1.50 netmask 0xffffff00 broadcast 192.168.1.255
                for (var i = 1; i < tokens.Length - 1; i++)
                {
                    if (tokens[i] != "netmask") continue;

                    var mask = tokens[i + 1];
                    if (current.SubnetMask is null && PlatformText.MaskToPrefix(mask) is not null)
                    {
                        current.SubnetMask = mask;
                    }
                }

                if (tokens.Length >= 2)
                {
                    var address = PlatformText.CleanIpValue(tokens[1]);
                    if (address is not null && !current.IPv4.Contains(address)) current.IPv4.Add(address);
                }

                continue;
            }

            if (trimmed.StartsWith("inet6 ", StringComparison.Ordinal))
            {
                var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2)
                {
                    var address = PlatformText.CleanIpValue(tokens[1]);
                    if (address is null) continue;
                    if (IPAddress.TryParse(address, out var parsed) &&
                        parsed.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(parsed) &&
                        !current.IPv6.Contains(address))
                    {
                        current.IPv6.Add(address);
                    }
                }
            }
        }

        return builders;
    }

    private static bool IsIgnoredInterface(string name) =>
        name is "lo0" or "lo1" or "lo2" ||
        (name.StartsWith("lo", StringComparison.Ordinal) && name.Length <= 3);

    private static IReadOnlyList<NetworkInterfaceInfo> MergeWithBcl(
        List<AdapterBuilder> builders,
        IReadOnlyList<NetworkInterfaceInfo> bclInterfaces)
    {
        var result = new List<NetworkInterfaceInfo>();
        var matched = new HashSet<NetworkInterfaceInfo>();

        foreach (var builder in builders)
        {
            var match = bclInterfaces.FirstOrDefault(i =>
                string.Equals(i.Name, builder.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) matched.Add(match);

            result.Add(new NetworkInterfaceInfo(
                builder.Name,
                match?.Description,
                builder.Mac ?? match?.MacAddress,
                builder.IPv4.Count > 0 ? builder.IPv4 : (match?.IPv4Addresses ?? Array.Empty<string>()),
                builder.IPv6.Count > 0 ? builder.IPv6 : (match?.IPv6Addresses ?? Array.Empty<string>()),
                builder.SubnetMask ?? match?.SubnetMask,
                builder.IsUp || (match?.IsUp ?? false),
                match?.SpeedBitsPerSecond,
                match?.InterfaceType ?? "Ethernet"));
        }

        foreach (var bcl in bclInterfaces)
        {
            if (!matched.Contains(bcl)) result.Add(bcl);
        }

        return result;
    }

    // ------------------------------------------------------------------ 网关

    private sealed record DefaultRoute(string Gateway, string? InterfaceName);

    private static DefaultRoute? ParseDefaultRoute(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string? gateway = null;
        string? interfaceName = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("gateway:", StringComparison.OrdinalIgnoreCase))
            {
                gateway = trimmed["gateway:".Length..].Trim();
            }
            else if (trimmed.StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
            {
                interfaceName = trimmed["interface:".Length..].Trim();
            }
        }

        if (gateway is null || !IPAddress.TryParse(gateway, out _)) return null;
        return new DefaultRoute(gateway, interfaceName);
    }

    private static DefaultRoute? ParseNetstatRoutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string? bestGateway = null;
        string? bestInterface = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;
            if (!string.Equals(tokens[0], "default", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IPAddress.TryParse(tokens[1], out _)) continue;

            bestGateway = tokens[1];
            bestInterface = tokens[^1];
            break;
        }

        return bestGateway is null ? null : new DefaultRoute(bestGateway, bestInterface);
    }

    // ------------------------------------------------------------------ DNS

    private static List<string> ParseScutilDns(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (!trimmed.StartsWith("nameserver[", StringComparison.OrdinalIgnoreCase)) continue;

            var colon = trimmed.IndexOf(':');
            if (colon < 0 || colon == trimmed.Length - 1) continue;

            var address = PlatformText.CleanIpValue(trimmed[(colon + 1)..]);
            if (address is null) continue;
            if (!result.Contains(address, StringComparer.OrdinalIgnoreCase)) result.Add(address);
        }

        return result;
    }
}
