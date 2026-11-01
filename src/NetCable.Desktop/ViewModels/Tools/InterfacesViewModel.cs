using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>网络接口：列出全部网卡的名称、MAC、IPv4 / IPv6、掩码、速率与状态。</summary>
[ToolPage("interfaces")]
public sealed partial class InterfacesViewModel : ToolViewModelBase
{
    private readonly INetworkInfoService _network = new NetworkInfoService();

    public InterfacesViewModel() : base("interfaces")
    {
        Summary = "点击「刷新接口」读取本机全部网卡";
    }

    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "正在读取网卡信息…";

        var info = await _network.GetNetworkInfoAsync(cancellationToken).ConfigureAwait(true);
        if (info is null)
        {
            Summary = "未获取到网络信息";
            LogError("网络信息服务返回空结果");
            return;
        }

        var up = 0;
        var index = 0;
        foreach (var iface in info.Interfaces)
        {
            index++;
            if (iface.IsUp) up++;

            var ipv4 = iface.IPv4Addresses.Count > 0 ? string.Join(" / ", iface.IPv4Addresses) : "—";
            var ipv6 = iface.IPv6Addresses.Count > 0 ? string.Join(" / ", iface.IPv6Addresses) : "—";
            var state = iface.IsUp ? "已启用" : "已禁用";
            var extra = $"MAC {iface.MacAddress ?? "—"}  ·  掩码 {iface.SubnetMask ?? "—"}  ·  " +
                        $"{DashboardViewModel.FormatSpeed(iface.SpeedBitsPerSecond)}  ·  {state}  ·  IPv6 {ipv6}";
            AddRow(iface.Name, ipv4, extra);

            if (iface.IsUp)
                LogOk($"{iface.Name}  {ipv4}  {state}  {DashboardViewModel.FormatSpeed(iface.SpeedBitsPerSecond)}");
            else
                LogMuted($"{iface.Name}  已禁用  ({iface.InterfaceType ?? "未知类型"})");
        }

        if (index == 0)
        {
            LogWarn("未检测到任何网络接口");
            Summary = "未检测到网络接口";
            return;
        }

        Summary = $"共 {index} 张网卡 · {up} 张已启用 · 当前 IP {info.CurrentIp ?? "—"} · " +
                  $"网关 {info.DefaultGateway ?? "—"} · DNS {info.DnsServers.Count} 个";
        LogInfo($"主机 {info.HostName} · 当前 IP {info.CurrentIp ?? "—"} · 网段 {info.SubnetCidr ?? "—"}");
        foreach (var dns in info.DnsServers) LogMuted($"DNS {dns}");
    }
}
