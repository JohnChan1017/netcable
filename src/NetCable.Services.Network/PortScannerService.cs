using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Utils;

namespace NetCable.Services.Network;

/// <summary>
/// 跨平台 TCP connect 端口扫描（三端行为一致，无需管理员/root 权限）。
/// <para>并发由 <see cref="SemaphoreSlim"/> 控制（上限 1024）；结果通过有界 Channel 流式回传，
/// 端口一开放就立即产出，不用等整轮扫完。</para>
/// <para>按契约只返回开放端口；若需要“开放+关闭全量”，用 <see cref="ScanIncludingClosedAsync"/>。</para>
/// <para>Banner：FTP/SSH/SMTP/POP3/IMAP/MySQL/Redis 等被动问候或主动探针直接读；
/// HTTP 端口发 HEAD 请求；HTTPS 端口做 TLS 握手后取证书 CN（明文读取对 443 无效）。</para>
/// </summary>
public sealed partial class PortScannerService : IPortScannerService
{
    /// <summary>单端口 banner 读取/主动探针的时间预算。</summary>
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>TLS 握手预算略大于普通 banner，握手本身要多一个 RTT。</summary>
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>DNS 解析预算。</summary>
    private static readonly TimeSpan HostLookupTimeout = TimeSpan.FromMilliseconds(3000);

    /// <summary>返回 banner 的最大长度，避免把二进制协议头整段塞进 UI。</summary>
    private const int BannerMaxLength = 160;

    /// <summary>并发上限，防止把线程池/文件句柄打爆。</summary>
    private const int PodScanConcurrencyLimit = 1024;

    /// <summary>需要尝试抓 banner 的端口（服务发现类端口）。</summary>
    private static readonly HashSet<int> BannerPorts = new()
    {
        21, 22, 23, 25, 80, 110, 143, 443, 465, 587, 993, 995,
        3306, 5432, 6379, 8080, 8443, 27017
    };

    /// <summary>明文 HTTP 端口：需要主动发 HEAD 才有响应。</summary>
    private static readonly HashSet<int> HttpPorts = new() { 80, 8000, 8080, 8888 };

    /// <summary>TLS 端口：只做握手取证书信息，明文读取拿不到 banner。</summary>
    private static readonly HashSet<int> TlsPorts = new() { 443, 465, 993, 995, 8443 };

    /// <summary>Redis：发一次 PING，正常情况下回 +PONG 作为可识别 banner。</summary>
    private const int RedisPort = 6379;

    /// <inheritdoc />
    public async IAsyncEnumerable<PortScanResult> ScanAsync(
        PortScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in ScanCoreAsync(options, includeClosed: false, cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    /// <summary>
    /// 额外能力（非 Core 契约）：连未开放端口一起返回。
    /// 默认 <see cref="ScanAsync"/> 只产出开放端口，避免 1-1024 这种范围刷出上千条“关闭”记录。
    /// </summary>
    public async IAsyncEnumerable<PortScanResult> ScanIncludingClosedAsync(
        PortScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in ScanCoreAsync(options, includeClosed: true, cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    private static async IAsyncEnumerable<PortScanResult> ScanCoreAsync(
        PortScanOptions options,
        bool includeClosed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = options.Host?.Trim() ?? string.Empty;
        if (host.Length == 0) yield break;

        var ports = BuildPortList(options);
        if (ports.Count == 0) yield break;

        var address = await NetworkHelpers
            .ResolveAddressAsync(host, HostLookupTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (address is null) yield break; // 主机不可解析：返回空结果而不是抛异常

        var concurrency = Math.Clamp(options.Concurrency, 1, PodScanConcurrencyLimit);
        var timeoutMs = Math.Clamp(options.TimeoutMs, 50, 60000);

        // scanCts 只用于“消费者提前停止时取消所有探测任务”；对外仍以调用方 token 为准。
        var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var channel = Channel.CreateBounded<PortScanResult>(new BoundedChannelOptions(Math.Max(16, concurrency * 2))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var probeTasks = new Task[ports.Count];
        for (var i = 0; i < ports.Count; i++)
        {
            probeTasks[i] = ProbePortAsync(address, host, ports[i], timeoutMs, options.ResolveService,
                includeClosed, channel.Writer, semaphore, scanCts.Token);
        }

        _ = CompleteWhenAllDoneAsync(Task.WhenAll(probeTasks), channel.Writer);

        try
        {
            while (await channel.Reader.WaitToReadAsync(scanCts.Token).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var result))
                    yield return result;
            }
        }
        finally
        {
            // 正常结束（writer 已完成）时这里是幂等空操作；提前 yield break / 异常时负责取消并收尾。
            scanCts.Cancel();
            channel.Writer.TryComplete();
        }
    }

    /// <summary>所有端口探测任务结束后关闭 channel，让读取端自然退出。</summary>
    private static async Task CompleteWhenAllDoneAsync(Task allDone, ChannelWriter<PortScanResult> writer)
    {
        try
        {
            await allDone.ConfigureAwait(false);
        }
        catch
        {
            // 单个端口异常已在探测任务内吞掉，这里只是兜底
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>探测单个端口：等并发信号量 → TCP connect → 抓 banner → 写入 channel。</summary>
    private static async Task ProbePortAsync(
        IPAddress address,
        string host,
        int port,
        int timeoutMs,
        bool resolveService,
        bool includeClosed,
        ChannelWriter<PortScanResult> writer,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var result = await ProbeAsync(address, host, port, timeoutMs, resolveService, cancellationToken)
                .ConfigureAwait(false);

            if (result.Open || includeClosed)
                await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消扫描：静默退出，取消状态由外层迭代器统一传递
        }
        catch
        {
            // 单个端口探测失败不影响其它端口
        }
        finally
        {
            if (acquired) semaphore.Release();
        }
    }

    /// <summary>TCP connect 探测单个端口；连接失败一律视为“关闭”，不抛异常。</summary>
    private static async Task<PortScanResult> ProbeAsync(
        IPAddress address,
        string host,
        int port,
        int timeoutMs,
        bool resolveService,
        CancellationToken cancellationToken)
    {
        var service = resolveService ? PortServiceLookup.Lookup(port) : null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var banner = await TryGrabBannerAsync(client, host, port, cancellationToken).ConfigureAwait(false);
            return new PortScanResult(port, true, service, banner, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 用户主动取消，交回外层统一处理
        }
        catch
        {
            return new PortScanResult(port, false, service, null, null);
        }
    }

    /// <summary>按端口类型抓 banner；任何异常都降级为 null，不影响“端口开放”这一结论。</summary>
    private static async Task<string?> TryGrabBannerAsync(
        TcpClient client,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (!BannerPorts.Contains(port)) return null;

        try
        {
            if (TlsPorts.Contains(port))
                return await TryReadTlsBannerAsync(client, host, cancellationToken).ConfigureAwait(false);

            var stream = client.GetStream();

            if (HttpPorts.Contains(port))
            {
                var request = Encoding.ASCII.GetBytes(
                    $"HEAD / HTTP/1.0\r\nHost: {host}\r\nUser-Agent: NetCable-PortScanner\r\nConnection: close\r\n\r\n");
                await TryWriteAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }
            else if (port == RedisPort)
            {
                await TryWriteAsync(stream, Encoding.ASCII.GetBytes("PING\r\n"), cancellationToken).ConfigureAwait(false);
            }

            return await ReadPlainBannerAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>发主动探针；失败（含超时）静默忽略，后面仍可能读到被动问候。</summary>
    private static async Task TryWriteAsync(NetworkStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeCts.CancelAfter(BannerTimeout);
            await stream.WriteAsync(payload, writeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 主动探针失败不关键
        }
    }

    /// <summary>读取一行明文 banner：只保留可打印 ASCII，最多 <see cref="BannerMaxLength"/> 字符。</summary>
    private static async Task<string?> ReadPlainBannerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[512];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(BannerTimeout);

            var read = await stream.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
            if (read <= 0) return null;

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstLine is null) return null;

            var cleaned = new string(firstLine.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
            if (cleaned.Length == 0) return null;

            return cleaned.Length > BannerMaxLength ? cleaned[..BannerMaxLength] : cleaned;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// HTTPS/SMTPS/IMAPS/POP3S 等 TLS 端口：完成握手后取证书简单名作为 banner。
    /// 证书校验故意放行（扫描器只做特征识别，不做信任判断）。
    /// </summary>
    private static async Task<string?> TryReadTlsBannerAsync(
        TcpClient client,
        string host,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TlsHandshakeTimeout);

            using var ssl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: true,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeoutCts.Token).ConfigureAwait(false);

            var subject = (ssl.RemoteCertificate as X509Certificate2)?.GetNameInfo(X509NameType.SimpleName, false);
            var description = $"TLS {ssl.SslProtocol}";
            return string.IsNullOrWhiteSpace(subject) ? description : $"{description} · {subject}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 生成待扫描端口列表：显式 <see cref="PortScanOptions.Ports"/> 优先，
    /// 否则使用 [StartPort, EndPort] 闭区间；过滤非法端口、去重、升序。
    /// </summary>
    private static List<int> BuildPortList(PortScanOptions options)
    {
        if (options.Ports is { Length: > 0 })
        {
            return options.Ports
                .Where(port => port is >= 1 and <= 65535)
                .Distinct()
                .OrderBy(port => port)
                .ToList();
        }

        var start = Math.Clamp(options.StartPort, 1, 65535);
        var end = Math.Clamp(options.EndPort, 1, 65535);
        if (end < start) return new List<int>();

        var ports = new List<int>(end - start + 1);
        for (var port = start; port <= end; port++) ports.Add(port);
        return ports;
    }
}
