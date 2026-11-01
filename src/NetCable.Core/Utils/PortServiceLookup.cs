using System.Xml.Linq;

namespace NetCable.Core.Utils;

/// <summary>端口 → 服务名映射，数据来自内置 Ports.xml。</summary>
public sealed class PortServiceLookup
{
    private static readonly Lazy<Dictionary<int, string>> Database = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? Lookup(int port) =>
        Database.Value.TryGetValue(port, out var name) ? name : null;

    private static Dictionary<int, string> Load()
    {
        var map = new Dictionary<int, string>();
        using var stream = typeof(PortServiceLookup).Assembly.GetManifestResourceStream("NetCable.Core.Resources.Ports.xml");
        if (stream is null) return map;

        var doc = XDocument.Load(stream);
        foreach (var node in doc.Descendants())
        {
            var numberText = node.Element("Number")?.Value ?? node.Element("Port")?.Value;
            var service = node.Element("Name")?.Value ?? node.Element("ServiceName")?.Value ?? node.Element("Service")?.Value;
            var protocol = node.Element("Protocol")?.Value;
            if (int.TryParse(numberText, out var port) && !string.IsNullOrWhiteSpace(service))
            {
                var key = port;
                var value = string.IsNullOrEmpty(protocol) ? service : $"{service}/{protocol.ToLowerInvariant()}";
                if (protocol is null || protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                    map[key] = value;
                else if (!map.ContainsKey(key)) map[key] = value;
            }
        }
        return map;
    }
}
