using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// macOS 邻居表读取器，解析 <c>arp -an</c>。
/// 行格式：<c>? (192.168.1.1) at aa:bb:cc:dd:ee:ff on en0 ifscope [ethernet]</c>；
/// permanent 条目与 incomplete 条目分别映射为 permanent / INCOMPLETE。
/// </summary>
internal static class MacNeighborReader
{
    private static readonly Regex RowRegex = new(
        @"\((?<ip>[^)]+)\)\s+at\s+(?<mac>[0-9a-fA-F:]+|\(incomplete\))\s+on\s+(?<iface>\S+)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>读取 macOS ARP 邻居表。</summary>
    public static async Task<IReadOnlyList<NeighborEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        var text = await NeighborReaders.CaptureAsync("arp", new[] { "-an" }, 10000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<NeighborEntry>();

        var result = new List<NeighborEntry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var match = RowRegex.Match(rawLine.TrimEnd('\r'));
            if (!match.Success) continue;

            var ip = match.Groups["ip"].Value.Trim();
            if (!System.Net.IPAddress.TryParse(ip, out _)) continue;

            var macText = match.Groups["mac"].Value;
            var rest = match.Groups["rest"].Value;
            string mac;
            string state;

            if (macText.StartsWith('('))
            {
                mac = NeighborReaders.UnknownMac;
                state = "INCOMPLETE";
            }
            else
            {
                mac = PlatformText.NormalizeMac(macText) ?? macText;
                state = rest.Contains("permanent", StringComparison.OrdinalIgnoreCase)
                    ? "permanent"
                    : "reachable";
            }

            result.Add(new NeighborEntry(ip, mac, state, match.Groups["iface"].Value));
        }

        return result;
    }
}
