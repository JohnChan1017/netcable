using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Utils;

namespace NetCable.Desktop.ViewModels.Tools;

[ToolPage("lookup")]
public sealed partial class LookupViewModel : ToolViewModelBase
{
    private readonly OuiLookup _oui = new();

    public LookupViewModel() : base("lookup")
    {
        Summary = $"内置 OUI 前缀库：{_oui.PrefixCount:N0} 条";
    }

    [ObservableProperty] private string _macAddress = "3C:5A:B4:11:22:33";
    [ObservableProperty] private string _summary = "—";

    [RelayCommand]
    private void Lookup()
    {
        ClearOutput();
        var normalized = OuiLookup.NormalizeMac(MacAddress);
        if (normalized is null || normalized.Length < 6)
        {
            LogError("MAC 地址格式无效（至少 6 位十六进制）");
            Summary = "—";
            return;
        }

        var formatted = OuiLookup.FormatMac(normalized);
        var prefix = normalized[..6];
        var vendor = _oui.LookupVendor(normalized);

        AddRow("输入", MacAddress.Trim());
        AddRow("规范化", formatted);
        AddRow("OUI 前缀", prefix + " (/" + 24 + ")");
        AddRow("厂商", vendor ?? "未知（未收录）");
        Summary = vendor is null ? $"前缀 {prefix} 未在库中" : $"厂商：{vendor}";
        if (vendor is null) LogWarn($"OUI {prefix} 未收录"); else LogOk($"OUI {prefix} → {vendor}");
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Lookup();
        return Task.CompletedTask;
    }
}
