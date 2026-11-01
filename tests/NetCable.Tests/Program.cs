using System.Diagnostics;
using NetCable.Core.Platform;
using NetCable.Core.Utils;

namespace NetCable.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        Section("SubnetCalculator");
        Test("192.168.1.100/24 网段", () =>
        {
            var i = new SubnetCalculator().Calculate("192.168.1.100/24");
            Equal("192.168.1.0", i.NetworkAddress);
            Equal("192.168.1.255", i.BroadcastAddress);
            Equal("255.255.255.0", i.Netmask);
            Equal(24, i.PrefixLength);
            Equal("192.168.1.1", i.FirstUsable!);
            Equal("192.168.1.254", i.LastUsable!);
            Equal(254L, i.UsableHosts);
            Equal(true, i.IsPrivate);
        });
        Test("10.0.0.0/8", () =>
        {
            var i = new SubnetCalculator().Calculate("10.0.0.0/8");
            Equal("10.0.0.0", i.NetworkAddress);
            Equal(16777214L, i.UsableHosts);
        });
        Test("/30 只有 2 个可用", () =>
        {
            var i = new SubnetCalculator().Calculate("192.168.1.5/30");
            Equal("192.168.1.4", i.NetworkAddress);
            Equal("192.168.1.7", i.BroadcastAddress);
            Equal(2L, i.UsableHosts);
        });
        Test("掩码写法等价于前缀", () =>
        {
            var a = new SubnetCalculator().Calculate("172.16.5.9/255.255.255.0");
            Equal(24, a.PrefixLength);
            Equal("172.16.5.0", a.NetworkAddress);
        });
        Test("非法输入抛异常", () =>
        {
            Throws(() => new SubnetCalculator().Calculate("999.1.1.1/24"));
            Throws(() => new SubnetCalculator().Calculate("192.168.1.0/33"));
        });
        Test("ExpandRange CIDR 展开", () =>
        {
            var list = new SubnetCalculator().ExpandRange("10.0.0.0/30");
            Equal(4, list.Count);
            Equal("10.0.0.0", list[0]);
            Equal("10.0.0.3", list[3]);
        });
        Test("ExpandRange 区间展开", () =>
        {
            var list = new SubnetCalculator().ExpandRange("192.168.1.1-192.168.1.5");
            Equal(5, list.Count);
            Equal("192.168.1.5", list[4]);
        });

        Section("OUI / MAC");
        Test("MAC 规范化", () =>
        {
            Equal("3C5AB4112233", OuiLookup.NormalizeMac("3c:5a:b4:11:22:33")!);
            Equal("3C:5A:B4:11:22:33", OuiLookup.FormatMac("3c5ab4112233"));
        });
        Test("内置 OUI 库可加载且能查到厂商", () =>
        {
            var oui = new OuiLookup();
            Greater(1000, oui.PrefixCount);
            var vendor = oui.LookupVendor("3C:5A:B4:11:22:33");
            NotNull(vendor);
            Console.WriteLine($"      3C:5A:B4 -> {vendor}");
        });
        Test("无效 MAC 返回 null", () =>
        {
            Equal(null, OuiLookup.NormalizeMac("xx"));
            Equal(null, new OuiLookup().LookupVendor("zz"));
        });

        Section("端口服务表");
        Test("常见端口有服务名", () =>
        {
            var http = PortServiceLookup.Lookup(80);
            NotNull(http);
            Console.WriteLine($"      80 -> {http}, 443 -> {PortServiceLookup.Lookup(443)}, 22 -> {PortServiceLookup.Lookup(22)}");
        });

        Section("位计算器");
        Test("十进制 255 转换", () =>
        {
            var r = BitCalculator.Convert("255");
            Equal(true, r.Success);
            Equal("11111111", r.Binary);
            Equal("FF", r.Hexadecimal);
            Equal("377", r.Octal);
            Equal(8, r.BitLength);
        });
        Test("0x / 0b / 0o 前缀", () =>
        {
            Equal("255", BitCalculator.Convert("0xFF").Decimal);
            Equal("10", BitCalculator.Convert("0b1010").Decimal);
            Equal("8", BitCalculator.Convert("0o10").Decimal);
        });
        Test("负数与位运算", () =>
        {
            Equal("-5", BitCalculator.Convert("-5").Decimal);
            Equal("8", BitCalculator.Binary("and", "12", "10")!.Decimal);
            Equal("14", BitCalculator.Binary("or", "12", "10")!.Decimal);
            Equal("6", BitCalculator.Binary("xor", "12", "10")!.Decimal);
            Equal("16", BitCalculator.Binary("shift-left", "4", "2")!.Decimal);
            Equal("4", BitCalculator.Binary("shift-right", "16", "2")!.Decimal);
        });
        Test("非法输入返回错误而不抛异常", () =>
        {
            Equal(false, BitCalculator.Convert("0xZZ").Success);
            Equal(false, BitCalculator.Convert("").Success);
            Equal(false, BitCalculator.Convert("0b102").Success);
        });

        Section("平台判定");
        Test("当前平台是 Windows（本机）", () =>
        {
            Equal(OsFamily.Windows, PlatformInfo.Family);
            Equal("Windows", PlatformInfo.DisplayName);
        });

        Console.WriteLine();
        Console.WriteLine(new string('-', 56));
        Console.WriteLine($"通过 {_passed} · 失败 {_failed}");
        foreach (var f in Failures) Console.WriteLine("  FAIL " + f);
        return _failed == 0 ? 0 : 1;
    }

    private static void Section(string name) => Console.WriteLine($"\n## {name}");

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  OK   {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"期望 {expected}，实际 {actual}");
    }

    private static void Greater(int min, int actual)
    {
        if (actual <= min) throw new Exception($"期望 > {min}，实际 {actual}");
    }

    private static void NotNull(object? value)
    {
        if (value is null) throw new Exception("期望非 null，实际 null");
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch { return; }
        throw new Exception("期望抛出异常，但正常返回");
    }
}
