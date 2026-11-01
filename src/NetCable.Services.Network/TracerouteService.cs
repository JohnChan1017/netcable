using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Platform;

namespace NetCable.Services.Network;

/// <summary>
/// 跨平台 Traceroute：逐跳流式返回 <see cref="TracerouteHop"/>。
/// <list type="bullet">
///   <item>Windows：<c>tracert -d -h {maxHops} -w {timeoutMs}</c>，tracert 每跳固定发 3 个探测包。</item>
///   <item>macOS / Linux：<c>traceroute -n -m {maxHops} -w {秒} -q {probes}</c>。
///         macOS 与 Linux iputils 的这几个参数语义一致（-w 均为秒）。</item>
///   <item>Linux 缺少 traceroute 时回退 <c>tracepath -n -m {maxHops}</c>（iputils 通常预装 tracepath）。</item>
/// </list>
/// 命令统一加“不解析名字”开关（<c>-d</c> / <c>-n</c>）：名字由本服务按需反查并带超时，
/// 从而避免 VPN/劫持 DNS 环境下系统命令在每一跳都卡住数秒。
/// </summary>
public sealed partial class TracerouteService : ITracerouteService
{
    /// <summary>单跳反向解析超时，超过就返回 null（不阻塞后续跳）。</summary>
    private static readonly TimeSpan ReverseLookupTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>目标主机名解析超时。</summary>
    private static readonly TimeSpan DestinationLookupTimeout = TimeSpan.FromMilliseconds(2500);

    /// <summary>Windows tracert 固定每跳发送 3 个探测包，且没有参数可以修改。</summary>
    private const int WindowsProbesPerHop = 3;

    [GeneratedRegex(@"^\s*(\d+)\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HopLineRegex();

    // 同时匹配数字 RTT 与超时星号，保证 “*  1 ms  *” 这类混合探测的顺序与槽位不丢失。
    [GeneratedRegex(@"(?<star>\*)|(?:<\s*)?(?<num>\d+(?:[.,]\d+)?)\s*ms",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RttRegex();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    // 先抓含冒号的候选片段，再用 IPAddress.TryParse 校验，兼容 ::1 / ::ffff:192.168.1.1 等写法。
    [GeneratedRegex(@"(?<![0-9A-Za-z:])[0-9A-Fa-f:.]*:[0-9A-Fa-f:.]*(?![0-9A-Za-z:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6CandidateRegex();

    [GeneratedRegex(@"^\s*(\d+)\??:\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TracepathLineRegex();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TracepathRttRegex();

    /// <inheritdoc />
    public async IAsyncEnumerable<TracerouteHop> TraceAsync(
        TracerouteOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = options.Host?.Trim() ?? string.Empty;
        if (host.Length == 0) yield break;

        var maxHops = Math.Clamp(options.MaxHops, 1, 64);
        var timeoutMs = Math.Clamp(options.TimeoutMs, 100, 60000);
        var probes = Math.Clamp(options.ProbesPerHop, 1, 10);

        // 先解析终点地址，用于逐跳判断 DestinationReached（解析失败不影响继续跟踪）。
        var destinationAddresses = await NetworkHelpers
            .ResolveAddressStringsAsync(host, DestinationLookupTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (PlatformInfo.IsWindows)
        {
            var args = new[]
            {
                "-d",
                "-h", maxHops.ToString(CultureInfo.InvariantCulture),
                "-w", timeoutMs.ToString(CultureInfo.InvariantCulture),
                host
            };

            await foreach (var line in ProcessRunner
                               .StreamLinesAsync("tracert", args, cancellationToken)
                               .ConfigureAwait(false))
            {
                var parsed = ParseHopLine(line, WindowsProbesPerHop);
                if (parsed is null) continue;

                yield return await CreateHopAsync(parsed.Value.Number, parsed.Value.Address, parsed.Value.Rtts,
                        options.ResolveHostNames, destinationAddresses, host, cancellationToken)
                    .ConfigureAwait(false);
            }

            yield break;
        }

        // 非 Windows：优先 traceroute（macOS/Linux 都有对应参数），缺失时 Linux 回退 tracepath。
        if (ProcessRunner.ResolveCommand("traceroute") is not null)
        {
            var waitSeconds = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0))
                .ToString(CultureInfo.InvariantCulture);
            var args = new[]
            {
                "-n",
                "-m", maxHops.ToString(CultureInfo.InvariantCulture),
                "-w", waitSeconds,
                "-q", probes.ToString(CultureInfo.InvariantCulture),
                host
            };

            await foreach (var hop in ParseTracerouteStreamAsync(args, destinationAddresses, host,
                               options.ResolveHostNames, probes, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return hop;
            }

            yield break;
        }

        if (ProcessRunner.ResolveCommand("tracepath") is not null)
        {
            var args = new[]
            {
                "-n",
                "-m", maxHops.ToString(CultureInfo.InvariantCulture),
                host
            };

            await foreach (var hop in ParseTracepathStreamAsync(args, destinationAddresses, host,
                               options.ResolveHostNames, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return hop;
            }
        }

        // 两个命令都不存在：返回空结果而不是抛异常（最小化容器里可能既无 traceroute 也无 tracepath）。
    }

    /// <summary>解析 traceroute（非 Windows）文本流。</summary>
    private static async IAsyncEnumerable<TracerouteHop> ParseTracerouteStreamAsync(
        IReadOnlyList<string> args,
        IReadOnlyList<string> destinationAddresses,
        string host,
        bool resolveHostNames,
        int probesPerHop,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ProcessRunner
                           .StreamLinesAsync("traceroute", args, cancellationToken)
                           .ConfigureAwait(false))
        {
            var parsed = ParseHopLine(line, probesPerHop);
            if (parsed is null) continue;

            yield return await CreateHopAsync(parsed.Value.Number, parsed.Value.Address, parsed.Value.Rtts,
                    resolveHostNames, destinationAddresses, host, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 解析 tracepath 文本流。tracepath 同一跳可能输出多行（多次探测/PMTU 信息），
    /// 因此按跳号聚合后再逐跳返回；首行 <c>[LOCALHOST] pmtu</c> 是本地信息，不是真实跳。
    /// </summary>
    private static async IAsyncEnumerable<TracerouteHop> ParseTracepathStreamAsync(
        IReadOnlyList<string> args,
        IReadOnlyList<string> destinationAddresses,
        string host,
        bool resolveHostNames,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pendingNumber = 0;
        string? pendingAddress = null;
        var pendingRtts = new List<long?>();

        await foreach (var line in ProcessRunner
                           .StreamLinesAsync("tracepath", args, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!TryParseTracepathLine(line, out var number, out var lineAddress, out var lineRtts)) continue;

            if (number > pendingNumber)
            {
                if (pendingNumber > 0)
                {
                    yield return await CreateHopAsync(pendingNumber, pendingAddress, pendingRtts.ToArray(),
                            resolveHostNames, destinationAddresses, host, cancellationToken)
                        .ConfigureAwait(false);
                }

                pendingNumber = number;
                pendingAddress = null;
                pendingRtts = new List<long?>();
            }
            else if (number < pendingNumber)
            {
                continue; // 跳号倒退（异常输出）直接忽略
            }

            pendingAddress ??= lineAddress;
            pendingRtts.AddRange(lineRtts);
        }

        if (pendingNumber > 0)
        {
            yield return await CreateHopAsync(pendingNumber, pendingAddress, pendingRtts.ToArray(),
                    resolveHostNames, destinationAddresses, host, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 解析单行 tracepath 输出。首行 <c>1?: [LOCALHOST] pmtu 1500</c> 属于本地 PMTU 信息，返回 false。
    /// </summary>
    private static bool TryParseTracepathLine(
        string line,
        out int number,
        out string? address,
        out IReadOnlyList<long?> rtts)
    {
        number = 0;
        address = null;
        rtts = Array.Empty<long?>();

        var match = TracepathLineRegex().Match(line);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out number)) return false;

        var rest = match.Groups[2].Value.Trim();

        // tracepath 第一行形如 "1?: [LOCALHOST] pmtu 1500"，只描述本地 PMTU，不是一跳。
        if (rest.Contains("[LOCALHOST]", StringComparison.OrdinalIgnoreCase)) return false;

        address = ExtractAddress(rest);

        var parsedRtts = new List<long?>();
        foreach (Match rttMatch in TracepathRttRegex().Matches(rest))
        {
            var raw = rttMatch.Groups[1].Value.Replace(',', '.');
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                parsedRtts.Add((long)Math.Round(ms));
        }

        // "no reply" 行没有任何 RTT，补一个 null 表示该探测无响应。
        if (parsedRtts.Count == 0 && rest.StartsWith("no reply", StringComparison.OrdinalIgnoreCase))
            parsedRtts.Add(null);

        rtts = parsedRtts;
        return true;
    }

    /// <summary>
    /// 解析单行 “跳号 + RTT + 地址”。返回 null 表示不是跳行（表头、统计、完成提示等）。
    /// <paramref name="expectedProbes"/> 用于把 RTT 槽位补齐/截断到探测次数；tracert 固定 3 次。
    /// </summary>
    private static (int Number, List<long?> Rtts, string? Address)? ParseHopLine(string line, int expectedProbes)
    {
        var match = HopLineRegex().Match(line);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var number)) return null;

        var rest = match.Groups[2].Value;

        var rtts = new List<long?>();
        foreach (Match rttMatch in RttRegex().Matches(rest))
        {
            if (rttMatch.Groups["star"].Success)
            {
                rtts.Add(null);
                continue;
            }

            var raw = rttMatch.Groups["num"].Value.Replace(',', '.');
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                rtts.Add((long)Math.Round(ms));
        }

        var address = ExtractAddress(rest);
        if (rtts.Count == 0 && address is null) return null;

        while (rtts.Count < expectedProbes) rtts.Add(null);
        if (rtts.Count > expectedProbes) rtts.RemoveRange(expectedProbes, rtts.Count - expectedProbes);

        return (number, rtts, address);
    }

    /// <summary>从一段文本里提取第一个合法 IP（先 IPv6 再 IPv4，避免把 IPv4 映射地址截断）。</summary>
    private static string? ExtractAddress(string text)
    {
        if (text.Contains(':'))
        {
            foreach (Match match in Ipv6CandidateRegex().Matches(text))
            {
                if (IPAddress.TryParse(match.Value, out var ip) &&
                    ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return match.Value;
                }
            }
        }

        foreach (Match match in Ipv4Regex().Matches(text))
        {
            if (IPAddress.TryParse(match.Value, out var ip) &&
                ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return match.Value;
            }
        }

        return null;
    }

    /// <summary>构造一跳结果（按需做 PTR 反查）。</summary>
    private static async Task<TracerouteHop> CreateHopAsync(
        int number,
        string? address,
        IReadOnlyList<long?> rtts,
        bool resolveHostNames,
        IReadOnlyList<string> destinationAddresses,
        string host,
        CancellationToken cancellationToken)
    {
        var hostName = resolveHostNames && address is not null
            ? await NetworkHelpers.ResolveNameAsync(address, ReverseLookupTimeout, cancellationToken).ConfigureAwait(false)
            : null;

        return new TracerouteHop(number, address, hostName, rtts,
            IsDestination(address, destinationAddresses, host));
    }

    /// <summary>判断该跳是否已到达终点：优先按解析出的 IP 比对，DNS 失败时退回字符串比对。</summary>
    private static bool IsDestination(
        string? address,
        IReadOnlyList<string> destinationAddresses,
        string host)
    {
        if (string.IsNullOrEmpty(address)) return false;

        if (destinationAddresses.Count > 0)
        {
            if (!IPAddress.TryParse(address, out var candidate)) return false;
            foreach (var destination in destinationAddresses)
            {
                if (IPAddress.TryParse(destination, out var parsed) && parsed.Equals(candidate)) return true;
            }

            return false;
        }

        return string.Equals(address, host.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
