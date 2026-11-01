using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetCable.Services.Platform.Internal;

/// <summary>平台输出解析的公共文本/IP 工具，全部为纯函数，解析失败返回 null 而不是抛异常。</summary>
internal static class PlatformText
{
    private static readonly Regex Ipv4Regex = new(
        @"(?<![\d.])((?:\d{1,3}\.){3}\d{1,3})(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MacRegex = new(
        @"(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingNumberRegex = new(
        @"-?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>把 12 位十六进制 MAC 归一化为 <c>aa:bb:cc:dd:ee:ff</c>；无法识别时原样返回。</summary>
    public static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var match = MacRegex.Match(raw);
        if (!match.Success)
        {
            var hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length != 12) return raw.Trim();
            raw = hex;
        }
        else
        {
            raw = match.Value;
        }

        var digits = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (digits.Length != 12) return raw.Trim();

        return string.Join(':', Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
    }

    /// <summary>点分十进制掩码或十六进制掩码（0xffffff00）转前缀长度。</summary>
    public static int? MaskToPrefix(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return null;

        mask = mask.Trim();
        uint value;
        if (mask.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(
                mask.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            value = hex;
        }
        else if (IPAddress.TryParse(mask, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
        else
        {
            return null;
        }

        var prefix = System.Numerics.BitOperations.PopCount(value);
        return prefix is >= 0 and <= 32 ? prefix : null;
    }

    /// <summary>前缀长度转点分十进制掩码。</summary>
    public static string PrefixToMask(int prefix)
    {
        prefix = Math.Clamp(prefix, 0, 32);
        var value = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
    }

    /// <summary>由 IP 与掩码/前缀得到 <c>192.168.1.10/24</c> 形式的 CIDR。</summary>
    public static string? MakeCidr(string? ipAddress, string? mask)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return null;
        var prefix = MaskToPrefix(mask);
        return prefix is null ? ipAddress : $"{ipAddress}/{prefix.Value}";
    }

    /// <summary>取出文本中第一个合法的 IPv4 地址。</summary>
    public static string? ExtractFirstIpv4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match match in Ipv4Regex.Matches(text))
        {
            if (IPAddress.TryParse(match.Groups[1].Value, out _)) return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>取出文本中全部合法且去重的 IPv4 地址。</summary>
    public static IReadOnlyList<string> ExtractIpv4List(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (Match match in Ipv4Regex.Matches(text))
        {
            var value = match.Groups[1].Value;
            if (IPAddress.TryParse(value, out _) && !result.Contains(value)) result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// 清洗命令输出里的单个 IP 值：去掉 <c>(Preferred)</c>/<c>(首选)</c> 之类的后缀、
    /// IPv6 的 <c>%12</c> 域标识，再校验合法性。
    /// </summary>
    public static string? CleanIpValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        var parenthesis = text.IndexOf('(');
        if (parenthesis > 0) text = text[..parenthesis];
        var scope = text.IndexOf('%');
        if (scope > 0) text = text[..scope];
        text = text.Trim().Trim(',').Trim();

        if (text.Length == 0 || !IPAddress.TryParse(text, out var address)) return null;
        return address.ToString();
    }

    /// <summary>是否为回环/未指定地址。</summary>
    public static bool IsLoopbackOrUnspecified(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return true;
        if (!IPAddress.TryParse(ipAddress, out var address)) return true;
        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }

    /// <summary>判断网卡是否是回环/伪接口（这些不应出现在网卡列表里）。</summary>
    public static bool IsLoopbackInterfaceName(string? name, string? description)
    {
        foreach (var candidate in new[] { name, description })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var lower = candidate.ToLowerInvariant();
            if (lower.Contains("loopback", StringComparison.Ordinal)
                || lower.Contains("pseudo-interface", StringComparison.Ordinal)
                || candidate.Contains("回环", StringComparison.Ordinal)
                || lower is "lo" or "lo0")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>从文本里提取第一个整数（支持负数）。</summary>
    public static int? ExtractFirstInteger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = LeadingNumberRegex.Match(text);
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>把信号值解析成 0-100 的百分比：支持 <c>80%</c> 与 <c>-45 dBm</c> 两种形态。</summary>
    public static int? ParseSignalPercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = ExtractFirstInteger(text);
        if (value is null) return null;

        if (text.Contains('%')) return Math.Clamp(value.Value, 0, 100);
        if (text.Contains("dBm", StringComparison.OrdinalIgnoreCase) || value.Value < 0)
            return RssiToPercent(value.Value);
        return Math.Clamp(value.Value, 0, 100);
    }

    /// <summary>RSSI(dBm) 近似换算为信号百分比。</summary>
    public static int RssiToPercent(int rssi) => Math.Clamp((rssi + 100) * 2, 0, 100);

    /// <summary>根据频率（MHz）推断频段。</summary>
    public static string? BandFromFrequency(long megahertz)
    {
        if (megahertz <= 0) return null;
        if (megahertz < 2500) return "2.4 GHz";
        if (megahertz < 5925) return "5 GHz";
        return "6 GHz";
    }

    /// <summary>由中心频率（MHz）推算信道号。</summary>
    public static int? ChannelFromFrequency(long megahertz)
    {
        if (megahertz <= 0) return null;
        if (megahertz == 2484) return 14;
        if (megahertz >= 2412 && megahertz < 2500) return (int)((megahertz - 2407) / 5);
        if (megahertz >= 5000 && megahertz < 5925) return (int)((megahertz - 5000) / 5);
        if (megahertz >= 5950 && megahertz <= 7125) return (int)((megahertz - 5950) / 5);
        return null;
    }

    /// <summary>根据信道号与（可选的）频段文本推断频段。</summary>
    public static string? BandFromChannel(int channel)
    {
        if (channel <= 0) return null;
        if (channel <= 14) return "2.4 GHz";
        if (channel <= 196) return "5 GHz";
        return "6 GHz";
    }
}
