using NetCable.Core.Models;
using NetCable.Core.Utils;
using NetCable.Services.Lookup;
using NetCable.Services.Network;
using NetCable.Services.Platform;

namespace NetCable.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Help();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var a = args.Skip(1).ToArray();
        try
        {
            switch (command)
            {
                case "ping":
                {
                    var request = new PingRequest { Host = a[0], Count = GetInt(a, 1, 4) };
                    Console.WriteLine($"PING {request.Host} x{request.Count}");
                    await foreach (var r in new PingService().PingAsync(request))
                        Console.WriteLine(r.Success
                            ? $"  #{r.Sequence,-3} {r.Address,-16} {r.RoundTripMs,5} ms  TTL={r.Ttl}"
                            : $"  #{r.Sequence,-3} timeout  {r.Error}");
                    return 0;
                }
                case "trace":
                {
                    var opts = new TracerouteOptions { Host = a[0], MaxHops = GetInt(a, 1, 20) };
                    Console.WriteLine($"TRACEROUTE {opts.Host} (max {opts.MaxHops})");
                    await foreach (var h in new TracerouteService().TraceAsync(opts))
                        Console.WriteLine($"  {h.Hop,2}  {h.Address ?? "*",-16} {string.Join("  ", h.RoundTripMs.Select(x => x is null ? "*" : $"{x}ms")),-22} {h.HostName}");
                    return 0;
                }
                case "ports":
                {
                    var spec = a.Length > 1 ? a[1] : "1-1024";
                    var explicitPorts = spec.Contains(',')
                        ? spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray()
                        : null;
                    var (start, end) = explicitPorts is null ? ParseRange(spec) : (0, 0);
                    var opts = new PortScanOptions
                    {
                        Host = a[0], StartPort = start, EndPort = end,
                        Ports = explicitPorts ?? Array.Empty<int>(),
                        TimeoutMs = 800, Concurrency = 128
                    };
                    Console.WriteLine($"PORTSCAN {opts.Host} {(explicitPorts is null ? $"{start}-{end}" : string.Join(",", explicitPorts))}");
                    var n = 0;
                    await foreach (var p in new PortScannerService().ScanAsync(opts))
                    {
                        Console.WriteLine($"  {p.Port,5}/tcp open  {p.Service ?? "?",-16} {p.LatencyMs,4}ms  {p.Banner}");
                        n++;
                    }
                    Console.WriteLine($"  总计 {n} 个开放端口");
                    return 0;
                }
                case "scan":
                {
                    var opts = new HostScanOptions { Range = a[0], TimeoutMs = 800, Concurrency = 64 };
                    Console.WriteLine($"HOSTSCAN {opts.Range}");
                    var n = 0;
                    await foreach (var h in new HostScannerService().ScanAsync(opts))
                    {
                        Console.WriteLine($"  {h.IpAddress,-16} {(h.IsAlive ? "up" : "down"),-5} {h.LatencyMs,5}ms  {h.HostName,-24} {h.MacAddress,-18} {h.Vendor}");
                        n++;
                    }
                    Console.WriteLine($"  共 {n} 台");
                    return 0;
                }
                case "dns":
                {
                    var type = a.Length > 1 ? Enum.Parse<DnsRecordType>(a[1], true) : DnsRecordType.A;
                    var opts = new DnsQueryOptions { Name = a[0], Type = type, Server = a.Length > 2 ? a[2] : null };
                    var res = await new DnsLookupService().QueryAsync(opts);
                    Console.WriteLine($"DNS {res.Name} {type} @ {res.Server}  ({res.Elapsed.TotalMilliseconds:F0}ms)");
                    if (res.Error is not null) Console.WriteLine("  ERROR " + res.Error);
                    foreach (var r in res.Records) Console.WriteLine($"  {r.Type,-6} {r.Ttl,6}  {r.Value}");
                    return res.Error is null ? 0 : 2;
                }
                case "whois":
                {
                    var res = await new WhoisService().QueryAsync(new WhoisOptions { Query = a[0] });
                    Console.WriteLine($"WHOIS {res.Query} @ {res.Server}");
                    if (res.Error is not null) Console.WriteLine("  ERROR " + res.Error);
                    foreach (var f in res.Fields.Take(12)) Console.WriteLine($"  {f.Key,-22}: {f.Value}");
                    Console.WriteLine($"  (raw {res.Raw.Length} chars)");
                    return 0;
                }
                case "rdap":
                {
                    var res = await new RdapService().QueryAsync(a[0]);
                    Console.WriteLine($"RDAP {res.Query} handle={res.Handle} name={res.Name}");
                    if (res.Error is not null) Console.WriteLine("  ERROR " + res.Error);
                    foreach (var f in res.Fields.Take(12)) Console.WriteLine($"  {f.Key,-22}: {f.Value}");
                    return 0;
                }
                case "geo":
                {
                    var res = await new IpGeolocationService().LookupAsync(a.Length > 0 ? a[0] : "");
                    Console.WriteLine($"{res.Ip}  {res.Country}/{res.Region}/{res.City}  {res.Isp}  {res.Asn}  ({res.Latitude},{res.Longitude})");
                    return 0;
                }
                case "sntp":
                {
                    var res = await new SntpService().QueryAsync(a.Length > 0 ? a[0] : "pool.ntp.org");
                    Console.WriteLine($"{res.Server}  server={res.ServerTime:O}  offset={res.Offset.TotalMilliseconds:F0}ms  rtt={res.RoundTrip.TotalMilliseconds:F0}ms {res.Error}");
                    return 0;
                }
                case "http":
                {
                    var res = await new HttpTlsService().InspectAsync(a[0]);
                    Console.WriteLine($"{res.Url} -> {res.FinalUrl}  {res.StatusCode} {res.StatusText}  {res.HttpVersion}  {res.ElapsedMs}ms");
                    if (res.Error is not null) Console.WriteLine("  ERROR " + res.Error);
                    foreach (var h in res.Headers.Take(10)) Console.WriteLine($"  {h.Name}: {h.Value}");
                    if (res.Certificate is { } c) Console.WriteLine($"  cert: {c.Subject} | {c.Issuer} | 有效期至 {c.NotAfter:yyyy-MM-dd}");
                    return 0;
                }
                case "ifaces":
                {
                    var info = await new NetworkInfoService().GetNetworkInfoAsync();
                    Console.WriteLine($"HOST {info.HostName}  gateway={info.DefaultGateway}  dns={string.Join(", ", info.DnsServers)}  ip={info.CurrentIp} {info.SubnetCidr}");
                    foreach (var i in info.Interfaces)
                        Console.WriteLine($"  {i.Name,-24} up={i.IsUp,-5} {string.Join(",", i.IPv4Addresses),-16} mac={i.MacAddress}");
                    return 0;
                }
                case "conn":
                {
                    foreach (var c in (await new ConnectionService().GetConnectionsAsync(false)).Take(25))
                        Console.WriteLine($"  {c.Protocol,-4} {c.LocalEndPoint,-24} -> {c.RemoteEndPoint,-24} {c.State,-12} pid={c.Pid} {c.ProcessName}");
                    return 0;
                }
                case "neigh":
                {
                    foreach (var n in await new NeighborService().GetAsync())
                        Console.WriteLine($"  {n.IpAddress,-16} {n.MacAddress,-18} {n.State,-12} {n.Interface}");
                    return 0;
                }
                case "wol":
                {
                    await new WakeOnLanService().SendAsync(new WakeOnLanOptions { MacAddress = a[0] });
                    Console.WriteLine("魔术包已发送: " + a[0]);
                    return 0;
                }
                case "subnet":
                {
                    var i = new SubnetCalculator().Calculate(a[0]);
                    Console.WriteLine($"{i.NetworkCidr}  mask={i.Netmask}  range={i.FirstUsable}-{i.LastUsable}  hosts={i.UsableHosts}  private={i.IsPrivate}");
                    return 0;
                }
                case "oui":
                {
                    var oui = new OuiLookup();
                    Console.WriteLine($"{a[0]} -> {oui.LookupVendor(a[0]) ?? "未知"}  (库 {oui.PrefixCount} 条)");
                    return 0;
                }
                default:
                    Console.WriteLine("未知命令: " + command);
                    Help();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int GetInt(string[] args, int index, int fallback) =>
        args.Length > index && int.TryParse(args[index], out var v) ? v : fallback;

    private static (int, int) ParseRange(string text)
    {
        if (text.Contains('-'))
        {
            var p = text.Split('-', 2);
            return (int.Parse(p[0]), int.Parse(p[1]));
        }
        var single = int.Parse(text);
        return (single, single);
    }

    private static void Help()
    {
        Console.WriteLine("""
            NetCable CLI —— 跨平台网络工具箱（命令行）
            用法: NetCable.Cli <command> [args]

              ping   <host> [count]             ICMP 探测
              trace  <host> [maxHops]           路由追踪
              ports  <host> [range]             端口扫描（如 1-1024 或 80,443）
              scan   <cidr|a-b>                 局域网存活扫描
              dns    <name> [type] [server]     DNS 查询（A/AAAA/MX/TXT/NS/CNAME/SOA/PTR/SRV/CAA）
              whois  <domain|ip>                注册信息
              rdap   <domain|ip>                结构化注册数据
              geo    [ip]                       IP 归属
              sntp   [server]                   NTP 时间
              http   <url>                      HTTP / TLS 检查
              ifaces                            本机网络接口
              conn                              连接列表
              neigh                             邻居表（ARP）
              wol    <mac>                      Wake on LAN
              subnet <cidr>                     子网计算
              oui    <mac>                      MAC 厂商查询
            """);
    }
}
