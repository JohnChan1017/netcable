using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Platform;
using BclPingOptions = System.Net.NetworkInformation.PingOptions;

namespace NetCable.Services.Network;

/// <summary>
/// 跨平台 Ping。Windows 用 .NET 托管 ICMP（结果最可靠）；
/// macOS/Linux 用系统 ping（免特权），解析 time=xx ms / ttl=xx。
/// </summary>
public sealed partial class PingService : IPingService
{
    [GeneratedRegex(@"time[=<]\s*([\d.,]+)\s*ms", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"ttl[=:]\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TtlRegex();

    [GeneratedRegex(@"from\s+([0-9a-fA-F:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromRegex();

    public async IAsyncEnumerable<PingReplyInfo> PingAsync(
        PingRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var host = request.Host.Trim();
        if (host.Length == 0) yield break;

        if (PlatformInfo.IsWindows)
        {
            for (var seq = 1; seq <= request.Count; seq++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PingReplyInfo reply;
                try
                {
                    reply = await ManagedPingAsync(host, request, seq, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    reply = new PingReplyInfo(seq, host, false, null, 0, ex.Message);
                }

                yield return reply;

                if (seq < request.Count && request.IntervalMs > 0)
                    await Task.Delay(request.IntervalMs, cancellationToken).ConfigureAwait(false);
            }

            yield break;
        }

        var interval = Math.Max(0.2, request.IntervalMs / 1000.0)
            .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        // -W 的单位两平台不一致：Linux iputils 是“秒”，macOS 是“毫秒”。
        // 这里必须按平台区分，否则 macOS 上 2000ms 超时会被当成 2ms，导致所有主机误判离线。
        var waitText = PlatformInfo.IsMacOS
            ? Math.Max(1, request.TimeoutMs).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Math.Max(1, request.TimeoutMs / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-c", request.Count.ToString(),
            "-i", interval,
            "-s", request.BufferSize.ToString(),
            "-W", waitText
        };
        if (PlatformInfo.IsMacOS) { args.Add("-t"); args.Add(request.Ttl.ToString()); }
        args.Add(host);

        var seqNo = 0;
        await foreach (var line in ProcessRunner.StreamLinesAsync("ping", args, cancellationToken).ConfigureAwait(false))
        {
            var timeMatch = TimeRegex().Match(line);
            if (!timeMatch.Success) continue;

            seqNo++;
            var ms = (long)Math.Round(
                double.Parse(timeMatch.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture));
            var fromMatch = FromRegex().Match(line);
            var address = fromMatch.Success ? fromMatch.Groups[1].Value : host;
            var ttlMatch = TtlRegex().Match(line);
            var ttl = ttlMatch.Success ? int.Parse(ttlMatch.Groups[1].Value) : 0;

            yield return new PingReplyInfo(seqNo, address, true, ms, ttl, null);
            if (seqNo >= request.Count) yield break;
        }

        while (seqNo < request.Count)
        {
            seqNo++;
            yield return new PingReplyInfo(seqNo, host, false, null, 0, "请求超时");
        }
    }

    private static async Task<PingReplyInfo> ManagedPingAsync(
        string host, PingRequest request, int sequence, CancellationToken cancellationToken)
    {
        using var ping = new Ping();

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault();

        if (target is null)
            return new PingReplyInfo(sequence, host, false, null, 0, "无法解析主机名");

        var buffer = new byte[Math.Clamp(request.BufferSize, 0, 65500)];
        Random.Shared.NextBytes(buffer);

        var options = new BclPingOptions(Math.Clamp(request.Ttl, 1, 255), request.DontFragment);
        var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(request.TimeoutMs), buffer, options, cancellationToken)
            .ConfigureAwait(false);

        return reply.Status == IPStatus.Success
            ? new PingReplyInfo(sequence, reply.Address?.ToString() ?? host, true,
                reply.RoundtripTime, reply.Options?.Ttl ?? request.Ttl, null)
            : new PingReplyInfo(sequence, reply.Address?.ToString() ?? host, false, null, 0,
                reply.Status.ToString());
    }
}
