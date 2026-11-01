using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>Web 控制台：把 URL 交给系统默认浏览器（UI 层直接调 Process，不依赖服务）。</summary>
[ToolPage("webconsole")]
public sealed partial class WebConsoleViewModel : ToolViewModelBase
{
    public WebConsoleViewModel() : base("webconsole") { }

    [ObservableProperty] private string _url = "http://192.168.1.1";
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _notice = string.Empty;

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Command = string.Empty;
        Notice = string.Empty;
        Summary = "—";

        var url = NormalizeUrl(Url);
        if (url is null)
        {
            Notice = "URL 无效：请输入 http:// 或 https:// 地址，例如 http://192.168.1.1";
            Summary = "URL 无效";
            LogError(Notice);
            return Task.CompletedTask;
        }

        Command = BuildOpenCommand(url);
        AddRow("目标 URL", url);
        AddRow("运行平台", $"{PlatformInfo.DisplayName} {PlatformInfo.Architecture}");
        AddRow("浏览器命令", Command);
        Summary = $"{new Uri(url).Host} · 调用系统默认浏览器";
        Notice = "点击「用系统浏览器打开」会交给系统默认浏览器处理；上方命令仅供复制或排查。";
        LogOk($"目标：{url}");
        LogMuted($"平台：{PlatformInfo.DisplayName}（{PlatformInfo.Architecture}）");
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        var url = NormalizeUrl(Url);
        if (url is null)
        {
            Notice = "URL 无效，无法打开";
            LogError(Notice);
            return;
        }

        try
        {
            if (PlatformInfo.IsWindows)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else
            {
                var program = PlatformInfo.IsMacOS ? "open" : "xdg-open";
                var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
                startInfo.ArgumentList.Add(url);
                Process.Start(startInfo);
            }

            LogOk($"已交给系统浏览器打开：{url}");
            StatusText = "已打开";
        }
        catch (Exception ex)
        {
            LogError($"打开浏览器失败：{ex.Message}");
            StatusText = "打开失败";
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        var url = NormalizeUrl(Url);
        if (url is null)
        {
            LogWarn("URL 无效，无法复制");
            return;
        }

        if (await ClipboardHelper.TrySetTextAsync(url))
            LogOk("URL 已复制到剪贴板");
        else
            LogError("复制失败：无法访问系统剪贴板");
    }

    private static string? NormalizeUrl(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text.Replace(" ", string.Empty);

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var normalized = uri.ToString();
        if (uri.AbsolutePath == "/" && normalized.EndsWith('/'))
            normalized = normalized[..^1];
        return normalized;
    }

    private static string BuildOpenCommand(string url) => PlatformInfo.Family switch
    {
        OsFamily.Windows => $"start \"\" \"{url}\"",
        OsFamily.MacOS => $"open \"{url}\"",
        OsFamily.Linux => $"xdg-open \"{url}\"",
        _ => $"open \"{url}\""
    };
}
