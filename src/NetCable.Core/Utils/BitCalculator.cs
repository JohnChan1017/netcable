using System.Globalization;
using System.Numerics;
using NetCable.Core.Models;

namespace NetCable.Core.Utils;

/// <summary>位/进制计算器：支持 0b / 0x / 0o 前缀与十进制输入，输出四种进制与位信息。</summary>
public static class BitCalculator
{
    public static BitCalculatorResult Convert(string? input)
    {
        var text = (input ?? string.Empty).Trim().Replace("_", string.Empty).Replace(" ", string.Empty);
        if (text.Length == 0)
            return Fail("请输入数值，例如 255、0xFF、0b1010、0o755");

        BigInteger value;
        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = ParseBase(text[2..], 16);
            else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                value = ParseBase(text[2..], 2);
            else if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                value = ParseBase(text[2..], 8);
            else if (text.StartsWith('-'))
                value = -ParseBase(text[1..], 10);
            else
                value = ParseBase(text, 10);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        var abs = BigInteger.Abs(value);
        var bits = abs.IsZero ? 1 : (int)abs.GetBitLength();

        return new BitCalculatorResult(
            true,
            null,
            ToBase(value, 2),
            value.ToString(CultureInfo.InvariantCulture),
            ToBase(value, 16),
            ToBase(value, 8),
            bits);
    }

    /// <summary>位运算：返回 a op b 的结果（用于 UI 的快速计算）。</summary>
    public static BitCalculatorResult? Binary(string op, string a, string b)
    {
        var left = Convert(a);
        var right = Convert(b);
        if (!left.Success || !right.Success) return left.Success ? right : left;

        var x = BigInteger.Parse(left.Decimal, CultureInfo.InvariantCulture);
        var y = BigInteger.Parse(right.Decimal, CultureInfo.InvariantCulture);

        BigInteger result = op switch
        {
            "and" => x & y,
            "or" => x | y,
            "xor" => x ^ y,
            "shift-left" => x << (int)y,
            "shift-right" => x >> (int)y,
            _ => throw new ArgumentException("未知运算: " + op)
        };

        var abs = BigInteger.Abs(result);
        var bits = abs.IsZero ? 1 : (int)abs.GetBitLength();
        return new BitCalculatorResult(true, null, ToBase(result, 2), result.ToString(CultureInfo.InvariantCulture),
            ToBase(result, 16), ToBase(result, 8), bits);
    }

    private static BitCalculatorResult Fail(string error) =>
        new(false, error, string.Empty, string.Empty, string.Empty, string.Empty, 0);

    private static BigInteger ParseBase(string digits, int radix)
    {
        if (digits.Length == 0) throw new FormatException("数值为空");

        BigInteger value = BigInteger.Zero;
        foreach (var c in digits)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };
            if (d < 0 || d >= radix)
                throw new FormatException($"'{c}' 不是合法的 {radix} 进制数字");
            value = value * radix + d;
        }
        return value;
    }

    private static string ToBase(BigInteger value, int radix)
    {
        if (radix is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(radix));
        if (value.IsZero) return "0";

        var negative = value.Sign < 0;
        var current = BigInteger.Abs(value);
        var chars = new List<char>();
        while (!current.IsZero)
        {
            var digit = (int)(current % radix);
            chars.Add((char)(digit < 10 ? '0' + digit : 'A' + digit - 10));
            current /= radix;
        }
        if (negative) chars.Add('-');
        chars.Reverse();
        return new string(chars.ToArray());
    }
}
