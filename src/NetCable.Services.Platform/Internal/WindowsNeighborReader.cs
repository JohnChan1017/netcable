using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Windows 邻居表读取器，解析 <c>arp -a</c>。
/// 接口标题行通过 “IP --- 0x序号” 定位（与系统语言无关），
/// 数据行是纯数字/十六进制/IP，仍可解析；类型列兼容中英文。
/// </summary>
internal static class WindowsNeighborReader
{
    private static readonly Regex InterfaceRegex = new(
        @"(?<ip>(?:\d{1,3}\.){3}\d{1,3})\s+---\s+0x[0-9a-fA-F]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RowRegex = new(
        @"^\s*(?<ip>(?:\d{1,3}\.){3}\d{1,3})\s+(?<mac>(?:[0-9a-fA-F]{2}-){5}[0-9a-fA-F]{2})\s+(?<type>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>读取 Windows ARP 邻居表。</summary>
    public static async Task<IReadOnlyList<NeighborEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        var text = await NeighborReaders.CaptureAsync("arp", new[] { "-a" }, 10000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<NeighborEntry>();

        var result = new List<NeighborEntry>();
        string? currentInterface = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var interfaceMatch = InterfaceRegex.Match(line);
            if (interfaceMatch.Success)
            {
                currentInterface = interfaceMatch.Groups["ip"].Value;
                continue;
            }

            var match = RowRegex.Match(line);
            if (!match.Success) continue;

            var ip = match.Groups["ip"].Value;
            var mac = PlatformText.NormalizeMac(match.Groups["mac"].Value) ?? match.Groups["mac"].Value;
            result.Add(new NeighborEntry(
                ip,
                mac,
                NeighborReaders.NormalizeArpType(match.Groups["type"].Value),
                currentInterface ?? string.Empty));
        }

        return result;
    }
}
