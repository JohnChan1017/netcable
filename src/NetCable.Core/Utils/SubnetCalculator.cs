using System.Net;
using System.Net.Sockets;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Core.Utils;

public sealed class SubnetCalculator : ISubnetCalculator
{
    public SubnetInfo Calculate(string cidrOrIp) => CalculateInternal(cidrOrIp);

    public IReadOnlyList<string> ExpandRange(string range, int maxHosts = 4096)
    {
        var result = new List<string>();
        range = range.Trim();
        if (range.Contains('/'))
        {
            var parts = range.Split('/', 2);
            if (!TryParseIPv4(parts[0], out var baseIp)) return result;
            if (!int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32) return result;
            var network = baseIp & MaskFor(prefix);
            var count = (long)(1UL << (32 - prefix));
            for (long i = 0; i < count && result.Count < maxHosts; i++)
                result.Add(FormatIPv4((uint)(network + i)));
            return result;
        }

        if (range.Contains('-'))
        {
            var parts = range.Split('-', 2);
            if (!TryParseIPv4(parts[0].Trim(), out var start)) return result;
            if (!TryParseIPv4(parts[1].Trim(), out var end) || end < start) return result;
            for (uint ip = start; ip <= end && result.Count < maxHosts; ip++)
                result.Add(FormatIPv4(ip));
            return result;
        }

        if (TryParseIPv4(range, out var single)) result.Add(FormatIPv4(single));
        return result;
    }

    public static SubnetInfo CalculateInternal(string input)
    {
        var raw = input.Trim();
        if (string.IsNullOrEmpty(raw)) throw new ArgumentException("请输入 IP 或 CIDR", nameof(input));

        uint baseIp;
        int prefix;

        if (raw.Contains('/'))
        {
            var parts = raw.Split('/', 2);
            if (!TryParseIPv4(parts[0], out baseIp))
                throw new ArgumentException("IP 地址格式无效");
            var maskPart = parts[1].Trim();
            if (maskPart.Contains('.'))
            {
                if (!TryParseIPv4(maskPart, out var maskIp) || !IsValidMask(maskIp))
                    throw new ArgumentException("掩码格式无效");
                prefix = MaskToPrefix(maskIp);
            }
            else if (!int.TryParse(maskPart, out prefix) || prefix is < 0 or > 32)
            {
                throw new ArgumentException("前缀长度必须在 0-32 之间");
            }
        }
        else
        {
            if (!TryParseIPv4(raw, out baseIp)) throw new ArgumentException("IP 地址格式无效");
            prefix = 24;
        }

        var mask = MaskFor(prefix);
        var network = baseIp & mask;
        var broadcast = network | ~mask;
        var total = (long)(1UL << (32 - prefix));
        var usable = prefix >= 31 ? total : Math.Max(0, total - 2);

        string? first = null, last = null;
        if (prefix <= 30)
        {
            first = FormatIPv4(network + 1);
            last = FormatIPv4(broadcast - 1);
        }

        return new SubnetInfo(
            input,
            FormatIPv4(network),
            FormatIPv4(broadcast),
            FormatIPv4(mask),
            prefix,
            first,
            last,
            total,
            usable,
            IsPrivate(network),
            $"{FormatIPv4(network)}/{prefix}");
    }

    private static bool TryParseIPv4(string text, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(text.Trim(), out var ip)) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    private static string FormatIPv4(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    private static uint MaskFor(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    private static bool IsValidMask(uint mask)
    {
        var inverted = ~mask;
        return (inverted & (inverted + 1)) == 0;
    }

    private static int MaskToPrefix(uint mask) => System.Numerics.BitOperations.PopCount(mask);

    private static bool IsPrivate(uint ip)
    {
        var b0 = (ip >> 24) & 0xFF;
        var b1 = (ip >> 16) & 0xFF;
        return b0 == 10
               || (b0 == 172 && b1 is >= 16 and <= 31)
               || (b0 == 192 && b1 == 168)
               || (b0 == 169 && b1 == 254)
               || b0 == 127;
    }
}
