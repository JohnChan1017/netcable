using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>仪表盘：一屏汇总系统信息、网络出口、网关 / DNS 与全部网卡。</summary>
[ToolPage("dashboard")]
public sealed partial class DashboardViewModel : ToolViewModelBase
{
    private readonly INetworkInfoService _network = new NetworkInfoService();

    public DashboardViewModel() : base("dashboard")
    {
        Summary = "点击「开始采集」读取本机系统与网络信息";
    }

    [ObservableProperty] private bool _includePublicIp = true;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "正在采集…";

        var failures = 0;
        SystemInfoSummary? system = null;
        NetworkInfo? network = null;

        try
        {
            system = await _network.GetSystemInfoAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            LogError($"系统信息读取失败：{ex.Message}");
        }

        if (system is not null)
        {
            AddRow("主机名", system.HostName, system.UserName ?? "—");
            AddRow("操作系统", system.OsDisplayName, system.Architecture);
            AddRow(".NET 运行时", system.DotNetVersion,
                system.IsElevated ? "管理员权限" : "标准权限");
            AddRow("WiFi 能力", system.WifiSupported ? "支持" : "不支持/未检测到");
            LogOk($"系统：{system.HostName} · {system.OsDisplayName} · {system.Architecture} · .NET {system.DotNetVersion}");
        }

        try
        {
            network = await _network.GetNetworkInfoAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            LogError($"网络信息读取失败：{ex.Message}");
        }

        if (network is not null)
        {
            AddRow("当前 IP", network.CurrentIp ?? "—", network.SubnetCidr ?? "—");
            AddRow("默认网关", network.DefaultGateway ?? "—");
            AddRow("DNS 服务器", network.DnsServers.Count > 0
                ? string.Join(" · ", network.DnsServers)
                : "—");
        }

        if (IncludePublicIp)
        {
            try
            {
                var publicIp = await _network.GetPublicIpAsync(cancellationToken).ConfigureAwait(true);
                AddRow("公网出口 IP", string.IsNullOrWhiteSpace(publicIp) ? "未获取" : publicIp, "公网服务探测");
                if (string.IsNullOrWhiteSpace(publicIp)) LogWarn("公网 IP 未获取到（网络受限或服务不可用）");
                else LogOk($"公网出口 IP：{publicIp}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                LogWarn($"公网 IP 获取失败：{ex.Message}");
            }
        }

        var onlineInterfaces = 0;
        if (network is not null)
        {
            var index = 0;
            foreach (var iface in network.Interfaces)
            {
                index++;
                if (iface.IsUp) onlineInterfaces++;

                var ipv4 = iface.IPv4Addresses.Count > 0 ? string.Join(" / ", iface.IPv4Addresses) : "—";
                var ipv6 = iface.IPv6Addresses.Count > 0 ? string.Join(" / ", iface.IPv6Addresses) : "—";
                var extra = $"{iface.InterfaceType ?? "网卡"} · {FormatSpeed(iface.SpeedBitsPerSecond)} · " +
                            $"{(iface.IsUp ? "已启用" : "已禁用")} · MAC {iface.MacAddress ?? "—"} · IPv6 {ipv6}";
                AddRow($"网卡 {index} · {iface.Name}", ipv4, extra);

                if (iface.IsUp && iface.IPv4Addresses.Count > 0)
                    LogInfo($"{iface.Name}  {ipv4}  {FormatSpeed(iface.SpeedBitsPerSecond)}");
            }

            if (index == 0) LogWarn("未检测到任何网络接口");
        }

        Summary = BuildSummary(system, network, onlineInterfaces, failures);
        if (failures == 0) LogOk("采集完成");
    }

    private static string BuildSummary(
        SystemInfoSummary? system, NetworkInfo? network, int onlineInterfaces, int failures)
    {
        var parts = new List<string>();
        if (system is not null)
            parts.Add($"主机 {system.HostName}");
        if (network is not null)
        {
            parts.Add($"网卡 {network.Interfaces.Count} 张（{onlineInterfaces} 启用）");
            parts.Add($"当前 {network.CurrentIp ?? "—"}");
            parts.Add($"网关 {network.DefaultGateway ?? "—"}");
        }

        if (parts.Count == 0) return "采集失败，请查看日志";
        if (failures > 0) parts.Add($"{failures} 项失败");
        return string.Join(" · ", parts);
    }

    internal static string FormatSpeed(long? bitsPerSecond)
    {
        if (bitsPerSecond is null or <= 0) return "速率未知";
        if (bitsPerSecond >= 1_000_000_000)
            return $"{bitsPerSecond.Value / 1_000_000_000.0:0.##} Gbps";
        if (bitsPerSecond >= 1_000_000)
            return $"{bitsPerSecond.Value / 1_000_000.0:0.#} Mbps";
        if (bitsPerSecond >= 1_000)
            return $"{bitsPerSecond.Value / 1_000.0:0.#} Kbps";
        return $"{bitsPerSecond.Value} bps";
    }
}
