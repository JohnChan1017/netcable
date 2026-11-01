using CommunityToolkit.Mvvm.ComponentModel;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>PowerShell / pwsh：调用系统 PowerShell，可选附加 -Command 参数。</summary>
[ToolPage("powershell")]
public sealed partial class PowerShellViewModel : RemoteClientPageViewModel
{
    public PowerShellViewModel() : base("powershell")
    {
        Target = string.Empty;
    }

    protected override string ClientId => "powershell";

    /// <summary>要在 PowerShell 中执行的命令（可留空，仅打开窗口）。</summary>
    [ObservableProperty] private string _arguments = "Get-NetIPConfiguration";

    protected override string BuildExtraArguments()
    {
        var args = Arguments.Trim();
        if (args.Length == 0) return string.Empty;
        return $"-Command \"{args.Replace("\"", "\\\"")}\"";
    }

    protected override string? LaunchNote =>
        "PowerShell 已在新窗口打开；带 -Command 参数时请把剪贴板中的命令粘贴执行（或直接使用已复制的完整命令）。";
}
