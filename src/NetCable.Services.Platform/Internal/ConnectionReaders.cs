using System.Diagnostics;
using NetCable.Core.Models;
using NetCable.Core.Platform;

namespace NetCable.Services.Platform.Internal;

/// <summary>连接读取器的平台分派与公共工具。</summary>
internal static class ConnectionReaders
{
    /// <summary>按当前系统读取全部 TCP/UDP 连接（不含过滤）。</summary>
    public static Task<IReadOnlyList<ConnectionInfo>> ReadAsync(CancellationToken cancellationToken) =>
        PlatformInfo.Family switch
        {
            OsFamily.Windows => WindowsConnectionReader.ReadAsync(cancellationToken),
            OsFamily.MacOS => MacConnectionReader.ReadAsync(cancellationToken),
            OsFamily.Linux => LinuxConnectionReader.ReadAsync(cancellationToken),
            _ => Task.FromResult<IReadOnlyList<ConnectionInfo>>(Array.Empty<ConnectionInfo>())
        };

    /// <summary>判断端点是否是通配远端（<c>*:*</c>、<c>0.0.0.0:0</c>、<c>[::]:0</c>）。</summary>
    public static bool IsWildcardEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return true;
        var value = endpoint.Trim();
        return value is "*:*" or "*.*" or "0.0.0.0:0" or "[::]:0" or "0.0.0.0:*";
    }

    /// <summary>解析 PID 对应的进程名，失败返回 null（系统进程/无权限时很常见）。</summary>
    public static string? ResolveProcessName(int? pid, Dictionary<int, string?> cache)
    {
        if (pid is null or <= 0) return null;
        if (cache.TryGetValue(pid.Value, out var cached)) return cached;

        // Windows 的 0/4 号进程无法用 Process API 打开，直接给出固定名称
        if (PlatformInfo.IsWindows)
        {
            if (pid == 0) return "System Idle Process";
            if (pid == 4) return "System";
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            name = process.ProcessName;
        }
        catch
        {
            // 受保护进程或已退出
        }

        cache[pid.Value] = name;
        return name;
    }

    /// <summary>从端点字符串中提取端口，例如 <c>192.168.1.2:443</c> → 443。</summary>
    public static int? ExtractPort(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var value = endpoint.Trim();
        if (value is "*:*" or "*.*") return null;

        var colon = value.LastIndexOf(':');
        if (colon < 0 || colon == value.Length - 1) return null;
        var portText = value[(colon + 1)..];
        return int.TryParse(portText, out var port) ? port : null;
    }
}
