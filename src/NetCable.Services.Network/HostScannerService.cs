using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Platform;
using NetCable.Core.Utils;

namespace NetCable.Services.Network;

/// <summary>
/// 局域网主机扫描（IP Scanner）：CIDR 或 <c>a-b</c> 区间 → 存活判定 → PTR 反查 → 邻居表 MAC/OUI。
/// <para><b>存活判定分强/弱证据</b>（本机 VPN 环境下 ICMP 完全不可用，只靠 ICMP 会全部误判离线）：</para>
/// <list type="number">
///   <item><b>强证据</b>：ICMP（复用 <see cref="PingService"/>）成功；或邻居表里存在该 IP 的 MAC
///         （二层可达，TCP 探测本身会触发 ARP 解析）。</item>
///   <item><b>弱证据</b>：TCP 连接常见端口成功，但必须能证明是“真正的直连”——
///         通过 <see cref="LocalNetworkContext"/> 校验连接源地址：源地址若属于 TUN/代理接口
///         （如 Meta Tunnel 的 198.18.0.1），或本地代答导致 0ms 且不与目标同子网，则<b>不采信</b>。
///         这样可避免 Clash/Meta 类 TUN 把不存在的 IP 全部“连接成功”造成的假阳性。</item>
/// </list>
/// <para>每个地址都会产出一条 <see cref="DiscoveredHost"/>（含 <see cref="DiscoveredHost.IsAlive"/> 状态），
/// 由 UI 决定如何呈现“在线/离线”；PTR 只对判定在线的主机做，避免给离线地址刷无意义 DNS 查询。</para>
/// </summary>
public sealed partial class HostScannerService : IHostScannerService
{
    /// <summary>单次扫描展开的主机数上限，避免误填 /8 这类网段把内存/耗时打爆。</summary>
    private const int MaxHostsPerScan = 4096;

    /// <summary>单台主机 PTR 反查超时。</summary>
    private static readonly TimeSpan NameLookupTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>读取邻居表的命令超时。</summary>
    private const int NeighborCommandTimeoutMs = 5000;

    private static readonly IReadOnlyDictionary<string, string> EmptyNeighborTable =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IPingService _ping;
    private readonly ISubnetCalculator _subnet;
    private readonly OuiLookup _oui = new();

    /// <summary>使用默认的 <see cref="PingService"/> 与 <see cref="SubnetCalculator"/>。</summary>
    public HostScannerService() : this(new PingService(), new SubnetCalculator())
    {
    }

    /// <summary>可注入版本（便于单测替换 ICMP 探测/范围展开实现）。</summary>
    public HostScannerService(IPingService ping, ISubnetCalculator subnet)
    {
        _ping = ping ?? throw new ArgumentNullException(nameof(ping));
        _subnet = subnet ?? throw new ArgumentNullException(nameof(subnet));
    }

    /// <summary>
    /// ICMP 不可用时的 TCP 存活兜底端口（任意一个 TCP connect 成功即认为主机在线）。
    /// 可在 UI/测试里覆盖，例如只扫 80/443 以缩短时间。
    /// </summary>
    public int[] TcpLivenessPorts { get; set; } = { 80, 443, 22, 445, 3389, 8080 };

    /// <summary>是否启用 TCP 存活兜底（默认启用；纯 ICMP 语义场景可关闭）。</summary>
    public bool EnableTcpLivenessProbe { get; set; } = true;

    /// <summary>是否把邻居表条目当作存活证据（默认启用；关掉则 ARP 只用于 MAC/厂商展示）。</summary>
    public bool UseNeighborTableAsLivenessEvidence { get; set; } = true;

    // Windows `arp -a`：192.168.1.1   00-11-22-33-44-55   dynamic
    [GeneratedRegex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9A-Fa-f]{2}(?:[-:][0-9A-Fa-f]{2}){5})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsArpRegex();

    // macOS `arp -an` / Linux net-tools `arp -an`：? (192.168.1.1) at 0:11:22:33:44:55 on en0 ...
    [GeneratedRegex(@"\(\s*(\d{1,3}(?:\.\d{1,3}){3})\s*\)\s+at\s+([0-9A-Fa-f:]{2,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex BsdArpRegex();

    // Linux `ip neigh`：192.168.1.1 dev eth0 lladdr 00:11:22:33:44:55 REACHABLE
    [GeneratedRegex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+dev\s+\S+(?:\s+lladdr\s+([0-9A-Fa-f:]{2,}))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex IpNeighRegex();

    /// <inheritdoc />
    public async IAsyncEnumerable<DiscoveredHost> ScanAsync(
        HostScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var range = options.Range?.Trim() ?? string.Empty;
        if (range.Length == 0) yield break;

        IReadOnlyList<string> hosts;
        try
        {
            hosts = ExpandHosts(range);
        }
        catch
        {
            yield break; // 非法网段：返回空结果，不让 UI 进程挂掉
        }

        if (hosts.Count == 0) yield break;

        var concurrency = Math.Clamp(options.Concurrency, 1, 256);
        var timeoutMs = Math.Clamp(options.TimeoutMs, 100, 60000);

        // 本机接口快照：用于识别 TUN/代理“本地代答”造成的假 TCP 成功（见 LocalNetworkContext）。
        var networkContext = CreateNetworkContext();

        // 分批探测：批内并发（受 Concurrency 约束），批间串行。
        // 这样既保证输出顺序稳定，也能在每批探测之后立刻读一次邻居表 —— ARP 条目是 TCP/ICMP
        // 触发解析后才写入的，扫描前读取会拿不到新主机的 MAC。
        for (var offset = 0; offset < hosts.Count; offset += concurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(concurrency, hosts.Count - offset);
            var batch = new string[count];
            for (var i = 0; i < count; i++) batch[i] = hosts[offset + i];

            var probes = await Task
                .WhenAll(batch.Select(ip => ProbeHostAsync(ip, timeoutMs, networkContext, cancellationToken)))
                .ConfigureAwait(false);

            var neighbors = options.ResolveMacAddresses
                ? await ReadNeighborTableAsync(cancellationToken).ConfigureAwait(false)
                : EmptyNeighborTable;

            // 合并三条证据：MAC 条目存在 ⇒ 二层可达 ⇒ 在线（本机 ICMP 不可用时的主要依据）。
            var entries = new BatchEntry[probes.Length];
            for (var i = 0; i < probes.Length; i++)
            {
                neighbors.TryGetValue(probes[i].IpAddress, out var mac);
                var alive = probes[i].IsAlive
                            || (UseNeighborTableAsLivenessEvidence && mac is not null);
                entries[i] = new BatchEntry(probes[i], mac, alive);
            }

            // 只给在线主机做 PTR 反查，且在批内并发执行，避免逐台串行拖慢扫描。
            string?[] hostNames;
            if (options.ResolveHostNames)
            {
                var nameTasks = new Task<string?>[entries.Length];
                for (var i = 0; i < entries.Length; i++)
                {
                    nameTasks[i] = entries[i].IsAlive
                        ? NetworkHelpers.ResolveNameAsync(entries[i].Probe.IpAddress, NameLookupTimeout,
                            cancellationToken)
                        : Task.FromResult<string?>(null);
                }

                hostNames = await Task.WhenAll(nameTasks).ConfigureAwait(false);
            }
            else
            {
                hostNames = new string?[entries.Length];
            }

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var vendor = entry.Mac is null ? null : _oui.LookupVendor(entry.Mac);
                yield return new DiscoveredHost(
                    entry.Probe.IpAddress,
                    entry.IsAlive,
                    entry.Probe.LatencyMs,
                    hostNames[i],
                    entry.Mac,
                    vendor);
            }
        }
    }

    /// <summary>创建本机接口快照；枚举失败时退化为空快照（此时仅凭 TCP 不判定存活，保持保守）。</summary>
    private static LocalNetworkContext CreateNetworkContext()
    {
        try
        {
            return LocalNetworkContext.Create();
        }
        catch
        {
            return LocalNetworkContext.Empty;
        }
    }

    /// <summary>
    /// 探测单台主机是否在线：先 ICMP（PingService），失败再并发 TCP 连接常见端口做弱证据兜底。
    /// ARP 证据在批末统一合并（见 <see cref="ScanAsync"/>）。
    /// </summary>
    private async Task<ProbeResult> ProbeHostAsync(
        string ipAddress,
        int timeoutMs,
        LocalNetworkContext networkContext,
        CancellationToken cancellationToken)
    {
        // ---- 证据 1：ICMP ----
        var request = new PingRequest
        {
            Host = ipAddress,
            Count = 1,
            TimeoutMs = timeoutMs,
            BufferSize = 32,
            Ttl = 64,
            IntervalMs = 0,
            DontFragment = true
        };

        PingReplyInfo? reply = null;
        try
        {
            await foreach (var item in _ping.PingAsync(request, cancellationToken).ConfigureAwait(false))
            {
                reply = item;
                break; // 只要第一个（也是唯一一个）结果
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 用户取消，直接向上传递
        }
        catch
        {
            // 本机（VPN TUN）下 ICMP 可能整体不可用，按“本条证据不可用”处理，不影响 TCP/ARP 兜底
        }

        if (reply?.Success == true)
            return new ProbeResult(ipAddress, true, reply.RoundTripMs);

        // ---- 证据 2：TCP 弱证据兜底（ICMP 不可用或被防火墙丢弃时） ----
        if (EnableTcpLivenessProbe)
        {
            var (alive, latencyMs) = await ProbeTcpLivenessAsync(ipAddress, timeoutMs, networkContext,
                    cancellationToken)
                .ConfigureAwait(false);
            if (alive) return new ProbeResult(ipAddress, true, latencyMs);
        }

        return new ProbeResult(ipAddress, false, null);
    }

    /// <summary>
    /// 并发连接若干常见端口；只有能证明“真正直连”的成功才算存活（TUN/代理会本地代答）。
    /// 返回首个可信连接的耗时。
    /// </summary>
    private async Task<(bool Alive, long? LatencyMs)> ProbeTcpLivenessAsync(
        string ipAddress,
        int timeoutMs,
        LocalNetworkContext networkContext,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ipAddress, out var address)) return (false, null);

        var ports = TcpLivenessPorts;
        if (ports is null || ports.Length == 0) return (false, null);

        // 单次连接超时限制在 3s 内，避免 options.TimeoutMs 很大时把整轮扫描拖死
        var connectTimeoutMs = Math.Clamp(timeoutMs, 100, 3000);
        var attempts = new Task<TcpConnectResult?>[ports.Length];
        for (var i = 0; i < ports.Length; i++)
            attempts[i] = TryConnectAsync(address, ports[i], connectTimeoutMs, cancellationToken);

        try
        {
            var results = await Task.WhenAll(attempts).ConfigureAwait(false);
            foreach (var result in results)
            {
                // 关键：校验连接本端地址，排除 TUN/代理“本地代答”的 0ms 假成功
                if (result is null) continue;
                var probe = result.Value;
                if (!networkContext.IsDirectConnection(probe.LocalAddress, address, probe.LatencyMs)) continue;
                return (true, probe.LatencyMs);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 兜底失败：按离线处理
        }

        return (false, null);
    }

    /// <summary>TCP connect 计时并记录本端地址；失败/超时返回 null，不抛异常。</summary>
    private static async Task<TcpConnectResult?> TryConnectAsync(
        IPAddress address,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);

            var localAddress = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
            return new TcpConnectResult(stopwatch.ElapsedMilliseconds, localAddress);
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
    /// 展开扫描范围，并对 CIDR 剔除网络地址与广播地址（/31、/32 全部地址可用，不剔除）。
    /// </summary>
    private IReadOnlyList<string> ExpandHosts(string range)
    {
        var hosts = _subnet.ExpandRange(range, MaxHostsPerScan);
        if (hosts.Count == 0 || !range.Contains('/')) return hosts;

        try
        {
            var info = _subnet.Calculate(range);
            if (info.PrefixLength > 30) return hosts;

            return hosts
                .Where(ip => !string.Equals(ip, info.NetworkAddress, StringComparison.Ordinal)
                             && !string.Equals(ip, info.BroadcastAddress, StringComparison.Ordinal))
                .ToArray();
        }
        catch
        {
            return hosts;
        }
    }

    /// <summary>读取当前邻居表（IP → MAC）。命令缺失或解析失败时返回空表，不影响存活扫描。</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadNeighborTableAsync(
        CancellationToken cancellationToken)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (PlatformInfo.IsLinux)
            {
                // Linux 首选 iproute2 的 ip neigh（只包含已解析的 lladdr 条目）
                var neigh = await ProcessRunner
                    .RunAsync("ip", new[] { "neigh" }, NeighborCommandTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (neigh.Success) ParseNeighborLines(neigh.StandardOutput, table);

                // 最小化系统可能没装 iproute2：回退 net-tools 的 arp -an
                if (table.Count == 0)
                {
                    var arp = await ProcessRunner
                        .RunAsync("arp", new[] { "-an" }, NeighborCommandTimeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                    if (arp.Success) ParseNeighborLines(arp.StandardOutput, table);
                }
            }
            else if (PlatformInfo.IsMacOS)
            {
                // macOS 没有 ip neigh，标准的邻居表命令是 arp -an
                var arp = await ProcessRunner
                    .RunAsync("arp", new[] { "-an" }, NeighborCommandTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (arp.Success) ParseNeighborLines(arp.StandardOutput, table);
            }
            else
            {
                // Windows：arp -a（输出为 OEM 代码页，但 IP/MAC 均为 ASCII，ProcessRunner 的 UTF-8 解码不影响解析）
                var arp = await ProcessRunner
                    .RunAsync("arp", new[] { "-a" }, NeighborCommandTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (arp.Success) ParseNeighborLines(arp.StandardOutput, table);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 邻居表只是增强信息，失败不影响主机存活结果
        }

        return table;
    }

    /// <summary>逐行解析三种邻居表格式，写入 IP → 规范化 MAC；组播/广播地址跳过。</summary>
    private static void ParseNeighborLines(string output, IDictionary<string, string> table)
    {
        if (string.IsNullOrEmpty(output)) return;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            string? ip = null;
            string? macToken = null;

            var windowsMatch = WindowsArpRegex().Match(line);
            if (windowsMatch.Success)
            {
                ip = windowsMatch.Groups[1].Value;
                macToken = windowsMatch.Groups[2].Value;
            }
            else
            {
                var bsdMatch = BsdArpRegex().Match(line);
                if (bsdMatch.Success)
                {
                    ip = bsdMatch.Groups[1].Value;
                    macToken = bsdMatch.Groups[2].Value;
                }
                else
                {
                    var neighMatch = IpNeighRegex().Match(line);
                    if (neighMatch.Success && neighMatch.Groups[2].Success)
                    {
                        ip = neighMatch.Groups[1].Value;
                        macToken = neighMatch.Groups[2].Value;
                    }
                }
            }

            if (ip is null || macToken is null) continue;
            if (!IPAddress.TryParse(ip, out _)) continue;

            var mac = NetworkHelpers.NormalizeMac(macToken);
            if (mac is null) continue;

            // 跳过组播/广播（首字节最低位为 1），它们不可能对应真实网卡，也不该查 OUI
            var firstByte = Convert.ToByte(mac[..2], 16);
            if ((firstByte & 0x01) != 0) continue;

            table[ip] = mac;
        }
    }

    /// <summary>单台主机的探测结果（内部使用）。</summary>
    private readonly record struct ProbeResult(string IpAddress, bool IsAlive, long? LatencyMs);

    /// <summary>单次 TCP 连接结果：耗时 + 连接本端地址（用于判断是否真直连）。</summary>
    private readonly record struct TcpConnectResult(long LatencyMs, IPAddress? LocalAddress);

    /// <summary>合并邻居表证据之后的批次条目（内部使用）。</summary>
    private readonly record struct BatchEntry(ProbeResult Probe, string? Mac, bool IsAlive);
}
