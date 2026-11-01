namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>远程桌面（RDP）：调用系统 mstsc / xfreerdp / Windows App。</summary>
[ToolPage("rdp")]
public sealed partial class RdpViewModel : RemoteClientPageViewModel
{
    public RdpViewModel() : base("rdp")
    {
        Target = "192.168.1.1";
    }

    protected override string ClientId => "rdp";
}
