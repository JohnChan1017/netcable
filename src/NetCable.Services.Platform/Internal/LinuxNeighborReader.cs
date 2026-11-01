using System.Globalization;
using System.Net;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Linux 邻居表读取器：优先 <c>ip neigh show</c>，无 ip 命令时读 <c>/proc/net/arp</c>。
/// <c>ip neigh</c> 会同时返回 IPv4 与 IPv6 邻居。
/// </summary>
internal static class LinuxNeighborReader
{
    private static readonly HashSet<string> KnownStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "REACHABLE", "STALE", "DELAY", "PROBE", "FAILED", "INCOMPLETE",
        "NOARP", "PERMANENT", "PROXY", "UNKNOWN"
    };

    /// <summary>读取 Linux 邻居表。</summary>
    public static async Task<IReadOnlyList<NeighborEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        var text = await NeighborReaders.CaptureAsync("ip", new[] { "neigh", "show" }, 10000, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var parsed = ParseIpNeigh(text);
            if (parsed.Count > 0) return parsed;
        }

        return ReadProcArp();
    }

    private static IReadOnlyList<NeighborEntry> ParseIpNeigh(string text)
    {
        var result = new List<NeighborEntry>();

        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) continue;

            var ip = tokens[0];
            if (!IPAddress.TryParse(ip, out _)) continue;

            string? mac = null;
            string? device = null;
            for (var i = 1; i < tokens.Length - 1; i++)
            {
                if (tokens[i] == "dev")
                {
                    device = tokens[i + 1];
                }
                else if (tokens[i] == "lladdr")
                {
                    mac = tokens[i + 1];
                }
            }

            // 状态通常是行尾大写单词；含 router/nud 等附加标记时仍取最后一个
            var lastToken = tokens[^1].ToUpperInvariant();
            var state = KnownStates.Contains(lastToken) ? lastToken : "UNKNOWN";

            var macAddress = mac is null
                ? NeighborReaders.UnknownMac
                : PlatformText.NormalizeMac(mac) ?? mac;

            result.Add(new NeighborEntry(ip, macAddress, state, device ?? string.Empty));
        }

        return result;
    }

    private static IReadOnlyList<NeighborEntry> ReadProcArp()
    {
        var result = new List<NeighborEntry>();
        const string path = "/proc/net/arp";

        try
        {
            if (!File.Exists(path)) return result;

            var headerSkipped = false;
            foreach (var line in File.ReadAllLines(path))
            {
                if (!headerSkipped)
                {
                    headerSkipped = true;
                    continue;
                }

                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // IP address HW type Flags HW address Mask Device
                if (tokens.Length < 6) continue;
                if (!IPAddress.TryParse(tokens[0], out _)) continue;

                var mac = PlatformText.NormalizeMac(tokens[3]) ?? NeighborReaders.UnknownMac;
                var reachable = false;
                if (tokens[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(tokens[2].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
                {
                    reachable = (flags & 0x2) != 0;
                }

                result.Add(new NeighborEntry(tokens[0], mac, reachable ? "REACHABLE" : "INCOMPLETE", tokens[5]));
            }
        }
        catch
        {
            // 读取 /proc 失败时返回空列表
        }

        return result;
    }
}
