using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Utils;

namespace NetCable.Desktop.ViewModels.Tools;

[ToolPage("subnet")]
public sealed partial class SubnetViewModel : ToolViewModelBase
{
    private readonly SubnetCalculator _calculator = new();

    public SubnetViewModel() : base("subnet")
    {
        Compute();
    }

    [ObservableProperty] private string _input = "192.168.1.100/24";
    [ObservableProperty] private string _summary = "—";

    [RelayCommand]
    private void Compute()
    {
        ClearOutput();
        try
        {
            var info = _calculator.Calculate(Input);
            Summary = $"网段 {info.NetworkCidr} · 可用主机 {info.UsableHosts:N0} 个" + (info.IsPrivate ? " · 私有地址段" : " · 公网地址段");

            AddRow("输入", info.Input);
            AddRow("网络地址", info.NetworkAddress);
            AddRow("广播地址", info.BroadcastAddress);
            AddRow("子网掩码", info.Netmask);
            AddRow("前缀长度", $"/{info.PrefixLength}");
            AddRow("可用主机范围", info.FirstUsable is null ? "—" : $"{info.FirstUsable} — {info.LastUsable}");
            AddRow("地址总数", info.TotalAddresses.ToString("N0"));
            AddRow("可用主机数", info.UsableHosts.ToString("N0"));
            AddRow("地址类型", info.IsPrivate ? "私有（RFC1918）" : "公网");
            LogOk($"计算完成：{info.NetworkCidr}");
        }
        catch (Exception ex)
        {
            Summary = "—";
            LogError(ex.Message);
        }
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Compute();
        return Task.CompletedTask;
    }
}
