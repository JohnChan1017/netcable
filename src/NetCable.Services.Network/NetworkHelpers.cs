using System.Net;
using System.Net.Sockets;
using System.Text;
using NetCable.Core.Utils;

namespace NetCable.Services.Network;

/// <summary>
/// 网络服务层内部共用的地址解析、反向解析与 MAC 规范化工具。
/// 所有 DNS 操作都带超时/取消保护：本机 VPN（Meta Tunnel）会劫持 DNS，
/// 反向解析可能长时间不返回，必须限时避免拖垮整个扫描。
/// </summary>
internal static class NetworkHelpers
{
    /// <summary>把主机名或 IP 字面量解析为地址列表；失败（含超时）返回空列表，不抛异常。</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(
        string? host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return Array.Empty<IPAddress>();

        host = host.Trim();
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
            return addresses;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 用户主动取消，向上传递
        }
        catch
        {
            return Array.Empty<IPAddress>(); // DNS 失败/超时按“不可解析”处理
        }
    }

    /// <summary>解析主机名并优先返回 IPv4 地址（扫描/探测基本走 IPv4）；无结果返回 null。</summary>
    public static async Task<IPAddress?> ResolveAddressAsync(
        string? host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAddressesAsync(host, timeout, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault();
    }

    /// <summary>解析主机名并返回字符串地址列表（用于 traceroute 终点比对）。</summary>
    public static async Task<IReadOnlyList<string>> ResolveAddressStringsAsync(
        string? host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAddressesAsync(host, timeout, cancellationToken).ConfigureAwait(false);
        return addresses.Count == 0
            ? Array.Empty<string>()
            : addresses.Select(a => a.ToString()).ToArray();
    }

    /// <summary>
    /// PTR 反查。无记录、超时或返回 IP 本身（部分系统在无 PTR 记录时如此）都返回 null。
    /// </summary>
    public static async Task<string?> ResolveNameAsync(
        string? address,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var ip)) return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            // 注意：net10.0 的 Dns.GetHostEntryAsync 没有 (IPAddress, CancellationToken) 重载，
            // 传 IP 字符串同样会走 PTR 反查，并且能配合 CancellationToken 做超时控制。
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), timeoutCts.Token).ConfigureAwait(false);
            var name = entry.HostName?.Trim();
            if (string.IsNullOrEmpty(name)) return null;

            // 无 PTR 记录时 HostName 常回退成 IP 字符串，视为未解析。
            return string.Equals(name, ip.ToString(), StringComparison.OrdinalIgnoreCase) ? null : name;
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

    /// <summary>
    /// 把 ARP/邻居表里的 MAC 片段规范化为 <c>AA:BB:CC:DD:EE:FF</c>。
    /// 兼容 Windows 的 <c>00-11-22-33-44-55</c>、macOS 的 <c>0:11:22:33:44:55</c>（个位不补零）
    /// 以及标准冒号格式；格式非法返回 null。
    /// </summary>
    public static string? NormalizeMac(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Trim().Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6) return null;

        var builder = new StringBuilder(12);
        foreach (var part in parts)
        {
            if (part.Length == 1) builder.Append('0');      // macOS arp -an 会输出 0:11:... 这类缩写
            else if (part.Length != 2) return null;
            builder.Append(part);
        }

        var hex = builder.ToString();
        return hex.All(Uri.IsHexDigit) ? OuiLookup.FormatMac(hex) : null;
    }
}
