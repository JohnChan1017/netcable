namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>SSH / PuTTY：调用系统 OpenSSH 客户端。</summary>
[ToolPage("ssh")]
public sealed partial class SshViewModel : RemoteClientPageViewModel
{
    public SshViewModel() : base("ssh")
    {
        Target = "192.168.1.1";
    }

    protected override string ClientId => "ssh";
}
