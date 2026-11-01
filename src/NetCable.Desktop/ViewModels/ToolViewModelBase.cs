using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Tools;

namespace NetCable.Desktop.ViewModels;

/// <summary>所有工具页面的基类：统一「运行 / 取消 / 日志 / 结果」的状态机与输出模型。</summary>
public abstract partial class ToolViewModelBase : ViewModelBase
{
    private CancellationTokenSource? _cts;

    protected ToolViewModelBase(string toolId)
    {
        ToolId = toolId;
        Definition = ToolCatalog.All.FirstOrDefault(t => t.Id == toolId)
                     ?? new ToolDefinition(toolId, toolId, ToolGroup.Overview, string.Empty);
    }

    public string ToolId { get; }
    public ToolDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Description => Definition.Description;

    public ObservableCollection<LogEntry> Log { get; } = new();
    public ObservableCollection<ResultRow> Rows { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _hasOutput;

    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        HasOutput = true;
        _cts = new CancellationTokenSource();
        var started = DateTimeOffset.Now;
        try
        {
            await ExecuteAsync(_cts.Token).ConfigureAwait(true);
            StatusText = $"完成（{DateTimeOffset.Now - started:hh\\:mm\\:ss}）";
        }
        catch (OperationCanceledException)
        {
            LogMuted("已取消");
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            LogError(ex.Message);
            StatusText = "失败";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在停止…";
    }

    protected void ClearOutput()
    {
        Log.Clear();
        Rows.Clear();
    }

    protected void AddLog(LogKind kind, string text) =>
        Log.Add(new LogEntry(DateTimeOffset.Now, kind, text));

    protected void LogInfo(string text) => AddLog(LogKind.Info, text);
    protected void LogOk(string text) => AddLog(LogKind.Ok, text);
    protected void LogWarn(string text) => AddLog(LogKind.Warn, text);
    protected void LogError(string text) => AddLog(LogKind.Error, text);
    protected void LogMuted(string text) => AddLog(LogKind.Muted, text);

    protected void AddRow(string key, string value, string? extra = null) =>
        Rows.Add(new ResultRow(key, value, extra));
}
