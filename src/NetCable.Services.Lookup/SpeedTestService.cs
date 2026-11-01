using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Lookup;

/// <summary>
/// 测速服务（可选功能）。使用 Cloudflare 的 __down / __up 端点依次测延迟、下载、上传，
/// 以 IAsyncEnumerable 持续汇报进度；失败时产出一条 Phase 为“错误：…”的进度而不是抛异常。
/// </summary>
public sealed class SpeedTestService : ISpeedTestService
{
    private const string DownUrl = "https://speed.cloudflare.com/__down";
    private const string UpUrl = "https://speed.cloudflare.com/__up";
    private const int DownloadBytes = 25_000_000;
    private const int UploadBytes = 10_000_000;
    private const int TimeoutMs = 120_000;
    private const int ReportIntervalMs = 120;

    private readonly HttpClient _http;

    /// <summary>使用内置 HttpClient。</summary>
    public SpeedTestService() : this(null) { }

    /// <summary>可选注入 HttpClient（便于测试或替换测速端点传输层）。</summary>
    public SpeedTestService(HttpClient? httpClient)
    {
        _http = httpClient ?? LookupHttp.CreateClient(timeout: Timeout.InfiniteTimeSpan);
    }

    /// <summary>执行一次完整测速，逐条产出延迟/下载/上传进度与最终结果。</summary>
    public async IAsyncEnumerable<SpeedTestProgress> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1) 延迟
        double latency = 0;
        string? latencyError = null;
        try
        {
            latency = await MeasureLatencyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            latencyError = ex.Message;
        }

        if (latencyError is not null)
        {
            yield return ErrorProgress(latencyError);
            yield break;
        }

        yield return new SpeedTestProgress("延迟", 0.05, null, null, latency);

        // 2) 下载
        var download = new PhaseResult();
        var downloadError = new ErrorBox();
        await foreach (var progress in WrapAsync(DownloadPhaseAsync(download, latency, cancellationToken), downloadError, cancellationToken))
            yield return progress;

        if (downloadError.Value is not null)
        {
            yield return ErrorProgress(downloadError.Value);
            yield break;
        }

        yield return new SpeedTestProgress("下载", 0.7, download.Value, null, latency);

        // 3) 上传
        var upload = new PhaseResult();
        var uploadError = new ErrorBox();
        await foreach (var progress in WrapAsync(UploadPhaseAsync(upload, latency, cancellationToken), uploadError, cancellationToken))
            yield return progress;

        if (uploadError.Value is not null)
        {
            yield return ErrorProgress(uploadError.Value);
            yield break;
        }

        yield return new SpeedTestProgress("上传", 0.99, download.Value, upload.Value, latency);

        // 4) 完成
        yield return new SpeedTestProgress("完成", 1, download.Value, upload.Value, latency);
    }

    private async Task<double> MeasureLatencyAsync(CancellationToken cancellationToken)
    {
        var samples = new List<double>(3);
        for (var i = 0; i < 3; i++)
        {
            using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, 15000);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _http
                .GetAsync($"{DownUrl}?bytes=1000", HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            if (i < 2) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return samples.Count == 0 ? 0 : samples.Min();
    }

    private async IAsyncEnumerable<SpeedTestProgress> DownloadPhaseAsync(
        PhaseResult result,
        double latency,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, TimeoutMs);
        using var response = await _http
            .GetAsync($"{DownUrl}?bytes={DownloadBytes}", HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;

            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastReport < TimeSpan.FromMilliseconds(ReportIntervalMs)) continue;
            lastReport = elapsed;
            var fraction = 0.1 + 0.6 * Math.Min(1d, (double)total / DownloadBytes);
            yield return new SpeedTestProgress("下载", fraction, Mbps(total, elapsed), null, latency);
        }

        stopwatch.Stop();
        result.Value = Mbps(total, stopwatch.Elapsed);
    }

    private async IAsyncEnumerable<SpeedTestProgress> UploadPhaseAsync(
        PhaseResult result,
        double latency,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new byte[UploadBytes];
        Random.Shared.NextBytes(payload);
        var tracker = new UploadTracker(UploadBytes);

        using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, TimeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Post, UpUrl)
        {
            Content = new ProgressUploadContent(payload, tracker)
        };
        request.Headers.ExpectContinue = false;

        var stopwatch = Stopwatch.StartNew();
        var sendTask = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var lastReport = TimeSpan.Zero;

        while (!sendTask.IsCompleted)
        {
            await Task.WhenAny(sendTask, Task.Delay(ReportIntervalMs, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastReport < TimeSpan.FromMilliseconds(ReportIntervalMs)) continue;
            lastReport = elapsed;
            var fraction = 0.7 + 0.27 * tracker.Fraction;
            yield return new SpeedTestProgress("上传", fraction, null,
                Mbps(tracker.WrittenBytes, elapsed), latency);
        }

        using var response = await sendTask.ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        stopwatch.Stop();
        result.Value = Mbps(tracker.WrittenBytes, stopwatch.Elapsed);
    }

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        bytes * 8.0 / Math.Max(elapsed.TotalSeconds, 0.001) / 1_000_000.0;

    private static SpeedTestProgress ErrorProgress(string message) =>
        new("错误：" + LookupErrors.Truncate(message, 200), 1, null, null, null);

    private static async IAsyncEnumerable<SpeedTestProgress> WrapAsync(
        IAsyncEnumerable<SpeedTestProgress> source,
        ErrorBox error,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var moved = false;
            Exception? failure = null;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null)
            {
                error.Value = failure.Message;
                yield break;
            }
            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }

    private sealed class PhaseResult
    {
        public double Value { get; set; }
    }

    private sealed class ErrorBox
    {
        public string? Value { get; set; }
    }

    private sealed class UploadTracker
    {
        private readonly long _total;
        private long _written;

        public UploadTracker(long total) => _total = Math.Max(1, total);

        public long WrittenBytes => Interlocked.Read(ref _written);

        public double Fraction => Math.Min(1d, (double)WrittenBytes / _total);

        public void Report(long written) => Interlocked.Exchange(ref _written, written);
    }

    /// <summary>分块写入请求体并汇报已发送字节数的 HttpContent。</summary>
    private sealed class ProgressUploadContent : HttpContent
    {
        private const int ChunkSize = 65536;
        private readonly byte[] _payload;
        private readonly UploadTracker _tracker;

        public ProgressUploadContent(byte[] payload, UploadTracker tracker)
        {
            _payload = payload;
            _tracker = tracker;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < _payload.Length)
            {
                var count = Math.Min(ChunkSize, _payload.Length - written);
                await stream.WriteAsync(_payload.AsMemory(written, count), cancellationToken).ConfigureAwait(false);
                written += count;
                _tracker.Report(written);
            }
        }
    }
}
