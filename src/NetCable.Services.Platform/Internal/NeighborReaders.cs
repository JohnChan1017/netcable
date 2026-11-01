using NetCable.Core.Models;
using NetCable.Core.Platform;

namespace NetCable.Services.Platform.Internal;

/// <summary>邻居表（ARP/NDP 缓存）读取器的平台分派与公共工具。</summary>
internal static class NeighborReaders
{
    /// <summary>没有 MAC 时的占位文本（FAILED/INCOMPLETE 等条目）。</summary>
    public const string UnknownMac = "—";

    /// <summary>按当前系统读取邻居表。</summary>
    public static Task<IReadOnlyList<NeighborEntry>> ReadAsync(CancellationToken cancellationToken) =>
        PlatformInfo.Family switch
        {
            OsFamily.Windows => WindowsNeighborReader.ReadAsync(cancellationToken),
            OsFamily.MacOS => MacNeighborReader.ReadAsync(cancellationToken),
            OsFamily.Linux => LinuxNeighborReader.ReadAsync(cancellationToken),
            _ => Task.FromResult<IReadOnlyList<NeighborEntry>>(Array.Empty<NeighborEntry>())
        };

    /// <summary>把 Windows arp 的类型列归一化为 dynamic/static（兼容中英文输出）。</summary>
    public static string NormalizeArpType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();

        if (text.Contains("dynamic", StringComparison.OrdinalIgnoreCase)) return "dynamic";
        if (text.Contains("static", StringComparison.OrdinalIgnoreCase)) return "static";
        if (text.Contains("动态", StringComparison.Ordinal)) return "动态";
        if (text.Contains("静态", StringComparison.Ordinal)) return "静态";
        return text;
    }

    /// <summary>执行命令并返回合并输出；异常/超时返回 null。</summary>
    public static async Task<string?> CaptureAsync(
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
}
