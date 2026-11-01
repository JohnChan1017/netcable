using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Services.Lookup;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>测速：Cloudflare 端点测延迟 / 下行 / 上行，进度条 + 阶段日志实时刷新。</summary>
[ToolPage("speedtest")]
public sealed partial class SpeedTestViewModel : ToolViewModelBase
{
    private readonly ISpeedTestService _service = new SpeedTestService();

    public SpeedTestViewModel() : base("speedtest")
    {
        Summary = "点击「开始测速」：延迟 → 下行 → 上行（Cloudflare speed.cloudflare.com）";
    }

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _phase = "就绪";
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string _downloadText = "—";
    [ObservableProperty] private string _uploadText = "—";
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _endpointHint = "Cloudflare speed.cloudflare.com";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Progress = 0;
        Phase = "准备";
        LatencyText = "—";
        DownloadText = "—";
        UploadText = "—";
        Summary = "测速中…";

        LogInfo("开始测速：延迟 → 下行(25MB) → 上行(10MB)，端点 speed.cloudflare.com");
        var completed = false;

        await foreach (var progress in _service.RunAsync(cancellationToken).ConfigureAwait(true))
        {
            Progress = Math.Clamp(progress.Fraction, 0, 1) * 100;
            Phase = progress.Phase;

            if (progress.LatencyMs is { } latency) LatencyText = $"{latency:0} ms";
            if (progress.DownloadMbps is { } download) DownloadText = $"{download:0.##} Mbps";
            if (progress.UploadMbps is { } upload) UploadText = $"{upload:0.##} Mbps";

            if (progress.Phase.StartsWith("错误", StringComparison.Ordinal))
            {
                Summary = "测速失败：" + StripErrorPrefix(progress.Phase);
                Phase = "失败";
                EndpointHint = "Cloudflare 端点不可达";
                LogError(progress.Phase);
                LogWarn("本机 HTTPS 不可用（SChannel 证书链失败）时，Cloudflare 测速端点无法连接；这是环境限制，不是页面故障。");
                AddRowIfEmpty();
                Progress = 100;
                return;
            }

            if (progress.Phase == "完成")
            {
                completed = true;
                Summary = $"延迟 {LatencyText} · 下行 {DownloadText} · 上行 {UploadText}";
                LogOk(Summary);
                continue;
            }

            LogInfo($"{progress.Phase} {Progress:0}%  " +
                    $"延迟 {LatencyText} · 下行 {DownloadText} · 上行 {UploadText}");
        }

        if (!completed)
        {
            Summary = "测速结束，但未收到完整结果";
            LogWarn(Summary);
        }
    }

    private void AddRowIfEmpty()
    {
        // 失败时也给出结构化结果，便于用户复制排查信息。
        if (Rows.Count > 0) return;
        AddRow("状态", "失败", "无法完成测速");
        AddRow("延迟", "—", "Cloudflare HTTPS 端点不可达");
        AddRow("下行 / 上行", "— / —", "需要可用的 HTTPS 连接");
        AddRow("提示", "检查系统代理 / 证书链（SChannel）", "本机 https 全局失败时测速不可用");
    }

    private static string StripErrorPrefix(string phase)
    {
        var text = phase;
        var colon = text.IndexOfAny(new[] { '：', ':' });
        if (colon >= 0) text = text[(colon + 1)..];
        return text.Trim();
    }
}
