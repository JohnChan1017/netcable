using System.Xml.Linq;
using NetCable.Core.Contracts;

namespace NetCable.Core.Utils;

/// <summary>MAC/OUI 厂商查询，数据来自内置 OUI.xml（约 3.8MB，3.5 万条前缀）。</summary>
public sealed class OuiLookup : IOuiLookupService
{
    private static readonly Lazy<Dictionary<string, string>> Database = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public int PrefixCount => Database.Value.Count;

    public string? LookupVendor(string macAddress)
    {
        var normalized = NormalizeMac(macAddress);
        if (normalized is null) return null;
        var prefix = normalized.Length >= 6 ? normalized[..6] : normalized;
        return Database.Value.TryGetValue(prefix, out var vendor) ? vendor : null;
    }

    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length >= 6 ? hex : null;
    }

    public static string FormatMac(string? mac)
    {
        var hex = NormalizeMac(mac);
        if (hex is null) return mac ?? string.Empty;
        hex = hex[..12];
        var parts = Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2));
        return string.Join(":", parts);
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(OuiLookup).Assembly.GetManifestResourceStream("NetCable.Core.Resources.OUI.xml");
        if (stream is null) return map;

        var doc = XDocument.Load(stream);
        foreach (var node in doc.Root?.Elements("OUI") ?? Enumerable.Empty<XElement>())
        {
            var mac = node.Element("MACAddress")?.Value?.Trim();
            var vendor = node.Element("Vendor")?.Value?.Trim();
            if (!string.IsNullOrEmpty(mac) && !string.IsNullOrEmpty(vendor))
                map[mac] = vendor;
        }
        return map;
    }
}
