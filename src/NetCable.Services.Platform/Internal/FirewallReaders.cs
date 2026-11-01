using NetCable.Core.Models;
using NetCable.Core.Platform;

namespace NetCable.Services.Platform.Internal;

/// <summary>防火墙读取器的平台分派与公共映射工具（只读，不修改任何防火墙配置）。</summary>
internal static class FirewallReaders
{
    /// <summary>按当前系统读取防火墙状态。</summary>
    public static Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        PlatformInfo.Family switch
        {
            OsFamily.Windows => WindowsFirewallReader.GetStatusAsync(cancellationToken),
            OsFamily.MacOS => MacFirewallReader.GetStatusAsync(cancellationToken),
            OsFamily.Linux => LinuxFirewallReader.GetStatusAsync(cancellationToken),
            _ => Task.FromResult(new FirewallStatus(false, "当前平台不支持读取防火墙状态", null, null, null))
        };

    /// <summary>按当前系统读取防火墙规则（解析失败返回空列表）。</summary>
    public static Task<IReadOnlyList<FirewallRule>> GetRulesAsync(CancellationToken cancellationToken) =>
        PlatformInfo.Family switch
        {
            OsFamily.Windows => WindowsFirewallReader.GetRulesAsync(cancellationToken),
            OsFamily.MacOS => MacFirewallReader.GetRulesAsync(cancellationToken),
            OsFamily.Linux => LinuxFirewallReader.GetRulesAsync(cancellationToken),
            _ => Task.FromResult<IReadOnlyList<FirewallRule>>(Array.Empty<FirewallRule>())
        };

    /// <summary>把方向值归一化为「入站 / 出站 / 转发」；无法识别时原样返回。</summary>
    public static string MapDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();

        if (text.Contains("in", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("out", StringComparison.OrdinalIgnoreCase))
        {
            return "入站";
        }

        if (text.Contains("out", StringComparison.OrdinalIgnoreCase)) return "出站";
        if (text.Contains("forward", StringComparison.OrdinalIgnoreCase)) return "转发";
        if (text.Contains("入站", StringComparison.Ordinal)) return "入站";
        if (text.Contains("出站", StringComparison.Ordinal)) return "出站";
        if (text.Contains("转发", StringComparison.Ordinal)) return "转发";
        return text;
    }

    /// <summary>把动作值归一化为「允许 / 阻止 / 拒绝」；无法识别时原样返回。</summary>
    public static string MapAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();

        if (text.Equals("allow", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("accept", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("pass", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("permit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("允许", StringComparison.Ordinal))
        {
            return "允许";
        }

        if (text.Equals("block", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("drop", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("deny", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("阻止", StringComparison.Ordinal))
        {
            return "阻止";
        }

        if (text.Equals("reject", StringComparison.OrdinalIgnoreCase) || text.Contains("拒绝", StringComparison.Ordinal))
        {
            return "拒绝";
        }

        return text;
    }

    /// <summary>由链名推断方向：input→入站、output→出站、forward→转发。</summary>
    public static string DirectionFromChain(string? chain)
    {
        if (string.IsNullOrWhiteSpace(chain)) return string.Empty;
        var text = chain.Trim();

        if (text.Contains("input", StringComparison.OrdinalIgnoreCase)) return "入站";
        if (text.Contains("output", StringComparison.OrdinalIgnoreCase)) return "出站";
        if (text.Contains("forward", StringComparison.OrdinalIgnoreCase)) return "转发";
        return text;
    }

    /// <summary>端口值清洗：Any/任意/* 视为空。</summary>
    public static string? NormalizePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Equals("any", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("任意", StringComparison.Ordinal) ||
            text is "*" or "-")
        {
            return null;
        }

        return text;
    }

    /// <summary>远端地址清洗：Any/* 视为空，LocalSubnet 显示为中文。</summary>
    public static string? NormalizeRemoteAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Equals("any", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("任意", StringComparison.Ordinal) ||
            text is "*")
        {
            return null;
        }

        if (text.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)) return "本地子网";
        return text;
    }
}
