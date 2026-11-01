namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>VNC：调用系统 vncviewer / TigerVNC / macOS 屏幕共享。</summary>
[ToolPage("vnc")]
public sealed partial class VncViewModel : RemoteClientPageViewModel
{
    public VncViewModel() : base("vnc")
    {
        Target = "192.168.1.1";
    }

    protected override string ClientId => "vnc";
}
