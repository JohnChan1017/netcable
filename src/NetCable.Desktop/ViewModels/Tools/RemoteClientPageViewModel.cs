using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Models;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>把文本写入系统剪贴板；无窗口/无剪贴板时返回 false，不抛异常。</summary>
internal static class ClipboardHelper
{
    public static async Task<bool> TrySetTextAsync(string text)
    {
        try
        {
            var clipboard = (Application.Current?.ApplicationLifetime as
                IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
            if (clipboard is null) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 「调用系统原生客户端」类页面的统一基类：目标输入 → 客户端可用性 + 将执行的命令 → 复制 / 启动。
/// 具体页面只需给出 <see cref="ClientId"/>，并按需覆盖额外参数。
/// </summary>
public abstract partial class RemoteClientPageViewModel : ToolViewModelBase
{
    private readonly RemoteClientService _service = new();

    protected RemoteClientPageViewModel(string toolId) : base(toolId) { }

    /// <summary>RemoteClientService 使用的客户端 Id：rdp / ssh / vnc / powershell。</summary>
    protected abstract string ClientId { get; }

    [ObservableProperty] private string _target = string.Empty;
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _commandHint = string.Empty;
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _notice = string.Empty;
    [ObservableProperty] private bool _clientAvailable;

    /// <summary>命令末尾的额外参数（如 PowerShell 的 -Command），默认无。</summary>
    protected virtual string BuildExtraArguments() => string.Empty;

    /// <summary>启动成功后追加的提示（例如 PowerShell 需手动粘贴命令）。</summary>
    protected virtual string? LaunchNote => null;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Command = string.Empty;
        CommandHint = string.Empty;
        Notice = string.Empty;
        Summary = "—";
        ClientAvailable = false;

        var target = Target.Trim();
        LogInfo($"检测系统原生客户端（{ClientId}）…");

        IReadOnlyList<RemoteClientInfo> clients;
        try
        {
            clients = await _service.GetClientsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notice = $"客户端检测失败：{ex.Message}";
            Summary = "检测失败";
            LogError(Notice);
            return;
        }

        foreach (var client in clients)
        {
            var current = string.Equals(client.Id, ClientId, StringComparison.OrdinalIgnoreCase);
            AddRow($"{(current ? "▶ " : string.Empty)}{client.Title}",
                client.IsAvailable ? "可用" : "未安装",
                client.Executable ?? client.Hint ?? client.Id);
        }

        var info = clients.FirstOrDefault(c =>
            string.Equals(c.Id, ClientId, StringComparison.OrdinalIgnoreCase));
        if (info is null)
        {
            Notice = $"服务未返回 {ClientId} 客户端信息";
            Summary = "客户端不可用";
            LogWarn(Notice);
            return;
        }

        ClientAvailable = info.IsAvailable;
        CommandHint = info.Hint ?? string.Empty;

        if (!info.IsAvailable)
        {
            Notice = $"{info.Title} 不可用：{info.Hint ?? info.Executable ?? "未找到可执行文件"}";
            Summary = "未安装客户端";
            LogWarn(Notice);
            return;
        }

        string command;
        try
        {
            command = await _service.BuildCommandAsync(ClientId, target, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notice = $"生成命令失败：{ex.Message}";
            Summary = "命令生成失败";
            LogError(Notice);
            return;
        }

        var extra = BuildExtraArguments();
        Command = string.IsNullOrWhiteSpace(extra) ? command : $"{command} {extra}";
        Summary = $"{info.Title} 可用 · {info.Executable ?? ClientId}";
        if (!string.IsNullOrWhiteSpace(command))
            LogOk($"已生成命令：{Command}");
        else
            LogWarn("服务未能生成命令，请检查目标格式");
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (string.IsNullOrWhiteSpace(Command))
        {
            LogWarn("请先生成命令");
            return;
        }

        if (await ClipboardHelper.TrySetTextAsync(Command))
            LogOk("命令已复制到剪贴板");
        else
            LogError("复制失败：无法访问系统剪贴板");
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (!ClientAvailable)
        {
            LogWarn("客户端不可用，无法启动");
            return;
        }

        try
        {
            var note = await _service.LaunchAsync(ClientId, Target.Trim());
            LogOk(note);
            StatusText = "已启动";

            if (!string.IsNullOrWhiteSpace(Command) &&
                await ClipboardHelper.TrySetTextAsync(Command))
                LogMuted("命令已复制到剪贴板，可直接粘贴执行");

            if (LaunchNote is { Length: > 0 } launchNote)
                LogMuted(launchNote);
        }
        catch (Exception ex)
        {
            LogError($"启动失败：{ex.Message}");
            StatusText = "启动失败";
        }
    }
}
