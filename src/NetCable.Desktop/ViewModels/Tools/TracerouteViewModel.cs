using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>路由追踪：逐跳输出 RTT，并把每一跳实时写入日志。</summary>
[ToolPage("traceroute")]
public sealed partial class TracerouteViewModel : ToolViewModelBase
{
    private readonly ITracerouteService _traceroute = new TracerouteService();

    public TracerouteViewModel() : base("traceroute")
    {
        Summary = "输入目标主机后点击「开始追踪」";
    }

    [ObservableProperty] private string _host = "223.5.5.5";
    [ObservableProperty] private int _maxHops = 30;
    [ObservableProperty] private int _timeout = 1200;
    [ObservableProperty] private int _probes = 3;
    [ObservableProperty] private bool _resolveHostNames = true;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "追踪中…";

        var host = Host.Trim();
        if (host.Length == 0)
        {
            LogError("请输入目标主机或 IP");
            Summary = "—";
            return;
        }

        var options = new TracerouteOptions
        {
            Host = host,
            MaxHops = Math.Clamp(MaxHops, 1, 64),
            TimeoutMs = Math.Clamp(Timeout, 200, 10000),
            ProbesPerHop = Math.Clamp(Probes, 1, 5),
            ResolveHostNames = ResolveHostNames
        };

        LogInfo($"追踪 {options.Host}（最多 {options.MaxHops} 跳 / 每跳 {options.ProbesPerHop} 次探测 / 超时 {options.TimeoutMs}ms）");
        LogMuted("跳  地址                                    RTT");

        var hops = 0;
        var reached = false;
        var reachedHop = 0;
        string? lastAddress = null;

        await foreach (var hop in _traceroute.TraceAsync(options, cancellationToken).ConfigureAwait(true))
        {
            hops++;
            var rtts = hop.RoundTripMs.Count > 0
                ? string.Join("  ", hop.RoundTripMs.Select(ms => ms is null ? "*" : $"{ms}ms"))
                : "*";

            var line = new StringBuilder();
            line.Append($"{hop.Hop,2}  ");
            if (hop.Address is null) line.Append(rtts);
            else line.Append($"{hop.Address}  {rtts}");

            var hostName = hop.HostName;
            if (!string.IsNullOrWhiteSpace(hostName) &&
                !string.Equals(hostName, hop.Address, StringComparison.OrdinalIgnoreCase))
                line.Append($"  {hostName}");

            if (hop.DestinationReached)
            {
                line.Append("  ← 到达目标");
                LogOk(line.ToString());
            }
            else if (hop.Address is null)
            {
                LogMuted(line.ToString());
            }
            else
            {
                LogInfo(line.ToString());
            }

            var extra = new List<string> { rtts };
            if (!string.IsNullOrWhiteSpace(hostName) &&
                !string.Equals(hostName, hop.Address, StringComparison.OrdinalIgnoreCase))
                extra.Add(hostName!);
            if (hop.DestinationReached) extra.Add("到达目标");

            AddRow($"{hop.Hop,2}", hop.Address ?? "*  *  *", string.Join(" · ", extra));
            lastAddress = hop.Address ?? lastAddress;

            if (hop.Address is not null)
                StatusText = $"第 {hop.Hop} 跳：{hop.Address}";

            if (hop.DestinationReached)
            {
                reached = true;
                reachedHop = hop.Hop;
                break;
            }
        }

        if (hops == 0)
        {
            Summary = "未收到任何跳响应";
            LogWarn("未收到任何跳响应：目标可能不可达，或本机 ICMP 被防火墙 / 代理 TUN 拦截");
            return;
        }

        if (reached)
        {
            Summary = $"已到达 {host}：第 {reachedHop} 跳 {lastAddress}";
            LogOk(Summary);
        }
        else
        {
            Summary = $"未在 {options.MaxHops} 跳内到达 {host}（共收到 {hops} 跳响应）";
            LogWarn(Summary);
        }
    }
}
