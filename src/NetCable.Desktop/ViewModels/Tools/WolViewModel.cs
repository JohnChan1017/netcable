using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Core.Utils;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>Wake on LAN：构造魔术包并通过 UDP 广播唤醒设备。</summary>
[ToolPage("wol")]
public sealed partial class WolViewModel : ToolViewModelBase
{
    private readonly WakeOnLanService _service = new();
    private readonly OuiLookup _oui = new();

    public WolViewModel() : base("wol") { }

    [ObservableProperty] private string _macAddress = "3C:5A:B4:11:22:33";
    [ObservableProperty] private string _broadcastAddress = "255.255.255.255";
    [ObservableProperty] private int _port = 9;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "—";

        var normalized = OuiLookup.NormalizeMac(MacAddress);
        if (normalized is null || normalized.Length < 12)
        {
            LogError("MAC 地址无效：需要 12 位十六进制（例如 3C:5A:B4:11:22:33）");
            return;
        }

        var mac = OuiLookup.FormatMac(MacAddress);
        var broadcast = string.IsNullOrWhiteSpace(BroadcastAddress) ? "255.255.255.255" : BroadcastAddress.Trim();
        var vendor = _oui.LookupVendor(mac);

        LogInfo($"目标 {mac}{(vendor is null ? string.Empty : $"（{vendor}）")} → UDP {broadcast}:{Port}");

        await _service.SendAsync(
            new WakeOnLanOptions
            {
                MacAddress = mac,
                BroadcastAddress = broadcast,
                Port = Port
            },
            cancellationToken);

        LogOk($"魔术包已发送：6 × FF + 16 × {mac}");
        Summary = $"{mac} → {broadcast}:{Port} · 已发送";

        if (vendor is not null)
            LogMuted($"OUI 厂商：{vendor}");
    }
}
