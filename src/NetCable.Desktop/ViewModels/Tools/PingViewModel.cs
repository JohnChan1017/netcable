using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

[ToolPage("ping")]
public sealed partial class PingViewModel : ToolViewModelBase
{
    private readonly PingService _service = new();
    private readonly List<long> _rtts = new();

    public PingViewModel() : base("ping") { }

    [ObservableProperty] private string _host = "8.8.8.8";
    [ObservableProperty] private int _count = 10;
    [ObservableProperty] private int _timeout = 2000;
    [ObservableProperty] private int _bufferSize = 32;
    [ObservableProperty] private int _interval = 500;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        _rtts.Clear();
        Summary = "—";

        if (string.IsNullOrWhiteSpace(Host))
        {
            LogError("请输入目标主机");
            return;
        }

        LogInfo($"PING {Host}（{Count} 次，载荷 {BufferSize}B，超时 {Timeout}ms）");

        var sent = 0;
        var received = 0;
        var request = new PingRequest
        {
            Host = Host.Trim(),
            Count = Count,
            TimeoutMs = Timeout,
            BufferSize = BufferSize,
            IntervalMs = Interval
        };

        await foreach (var reply in _service.PingAsync(request, cancellationToken))
        {
            sent++;
            if (reply.Success)
            {
                received++;
                var ms = reply.RoundTripMs ?? 0;
                _rtts.Add(ms);
                LogOk($"#{reply.Sequence,-3} 来自 {reply.Address,-16} 时间={ms,4} ms  TTL={reply.Ttl}");
                Rows.Add(new ResultRow($"#{reply.Sequence}", reply.Address, $"{ms} ms  TTL {reply.Ttl}"));
            }
            else
            {
                LogWarn($"#{reply.Sequence,-3} 请求超时  {HumanizeError(reply.Error)}");
            }

            UpdateSummary(sent, received);
        }

        UpdateSummary(sent, received, final: true);
        LogInfo($"完成：发送 {sent}，接收 {received}，丢包率 {(sent == 0 ? 0 : (sent - received) * 100.0 / sent):F1}%");
    }

    private static string? HumanizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        if (error.Contains("exception occurred", StringComparison.OrdinalIgnoreCase)
            || error.Contains("transmit failed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("General failure", StringComparison.OrdinalIgnoreCase))
            return "ICMP 请求被拒绝（可能被 VPN / 防火墙 / 系统策略拦截，本机 Meta Tunnel 环境属正常现象）";
        if (error.Contains("TimedOut", StringComparison.OrdinalIgnoreCase)) return "超时";
        if (error.Contains("DestinationHostUnreachable", StringComparison.OrdinalIgnoreCase)) return "目标主机不可达";
        if (error.Contains("DestinationNetworkUnreachable", StringComparison.OrdinalIgnoreCase)) return "目标网络不可达";
        return error;
    }

    private void UpdateSummary(int sent, int received, bool final = false)
    {
        if (_rtts.Count == 0)
        {
            Summary = $"发送 {sent} · 接收 {received} · 丢包 {(sent == 0 ? 100 : (sent - received) * 100.0 / sent):F0}%";
            return;
        }

        var min = _rtts.Min();
        var max = _rtts.Max();
        var avg = _rtts.Average();
        Summary = $"发送 {sent} · 接收 {received} · 丢包 {(sent - received) * 100.0 / sent:F1}% · 最小 {min}ms · 平均 {avg:F1}ms · 最大 {max}ms";
        if (final) StatusText = Summary;
    }
}
