using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>局域网扫描：发现网段内存活主机并补充延迟、主机名、MAC 与厂商。</summary>
[ToolPage("ipscan")]
public sealed partial class IpScanViewModel : ToolViewModelBase
{
    private readonly IHostScannerService _scanner = new HostScannerService();

    public IpScanViewModel() : base("ipscan")
    {
        Summary = "支持 CIDR（192.168.1.0/24）与区间（192.168.1.1-192.168.1.50）";
    }

    [ObservableProperty] private string _range = "192.168.1.0/24";
    [ObservableProperty] private int _concurrency = 256;
    [ObservableProperty] private int _timeout = 900;

    // 默认关闭 PTR 反查：代理 / VPN 环境下反向解析经常超时，会把整轮扫描拖到十几秒；
    // 需要主机名的用户可勾选开启（在线主机才会反查）。
    [ObservableProperty] private bool _resolveHostNames;
    [ObservableProperty] private bool _resolveMacAddresses = true;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "扫描中…";

        var range = Range.Trim();
        if (range.Length == 0)
        {
            LogError("请输入网段（CIDR 或 a-b 区间）");
            Summary = "—";
            return;
        }

        var options = new HostScanOptions
        {
            Range = range,
            TimeoutMs = Math.Clamp(Timeout, 200, 10000),
            Concurrency = Math.Clamp(Concurrency, 1, 512),
            ResolveHostNames = ResolveHostNames,
            ResolveMacAddresses = ResolveMacAddresses
        };

        LogInfo($"扫描 {options.Range}（并发 {options.Concurrency}，超时 {options.TimeoutMs}ms，" +
                $"反向解析 {(options.ResolveHostNames ? "开" : "关")}，MAC {(options.ResolveMacAddresses ? "开" : "关")}）");

        var online = 0;
        var total = 0;
        var offline = new List<DiscoveredHost>();
        var started = DateTimeOffset.Now;

        await foreach (var host in _scanner.ScanAsync(options, cancellationToken).ConfigureAwait(true))
        {
            total++;
            if (host.IsAlive)
            {
                online++;
                AddHostRow(host);
                StatusText = $"在线 {online} / 已扫描 {total}…";
            }
            else
            {
                offline.Add(host);
            }
        }

        foreach (var host in offline) AddHostRow(host);

        var elapsed = DateTimeOffset.Now - started;
        Summary = $"扫描 {total} 台主机，在线 {online} 台" +
                  (total > online ? $"，离线 {total - online} 台" : string.Empty) +
                  $"（{elapsed.TotalSeconds:F1}s）";

        if (online == 0)
        {
            LogWarn("未发现存活主机：ICMP 可能被防火墙 / 代理 TUN 拦截，或网段内设备屏蔽了探测");
        }
        else
        {
            LogOk($"发现 {online} 台存活主机");
        }
    }

    private void AddHostRow(DiscoveredHost host)
    {
        var latency = host.LatencyMs is { } ms ? $"{ms} ms" : "—";
        var extra = new List<string> { latency };

        if (!string.IsNullOrWhiteSpace(host.HostName)) extra.Add(host.HostName!);
        if (!string.IsNullOrWhiteSpace(host.MacAddress)) extra.Add($"MAC {host.MacAddress}");
        if (!string.IsNullOrWhiteSpace(host.Vendor)) extra.Add(host.Vendor!);

        var status = host.IsAlive ? "在线" : "离线";
        AddRow(host.IpAddress, status, string.Join(" · ", extra));

        if (host.IsAlive)
        {
            LogOk($"{host.IpAddress,-15} 在线  {latency,-8} " +
                  $"{host.HostName ?? "—",-22} {host.MacAddress ?? "—",-18} {host.Vendor ?? "—"}");
        }
    }
}
