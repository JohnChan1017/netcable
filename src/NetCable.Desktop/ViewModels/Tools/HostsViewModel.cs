using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Models;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>hosts 文件条目的一行。</summary>
public sealed class HostsRow
{
    public HostsRow(HostsEntry entry)
    {
        Line = entry.LineNumber;
        IpAddress = entry.IpAddress;
        HostName = entry.HostName;
        Comment = entry.Comment ?? string.Empty;
    }

    public int Line { get; }
    public string IpAddress { get; }
    public string HostName { get; }
    public string Comment { get; }
}

/// <summary>Hosts 编辑：条目表 + 规范化文本编辑区，保存前自动备份（需管理员权限）。</summary>
[ToolPage("hosts")]
public sealed partial class HostsViewModel : ToolViewModelBase
{
    private readonly HostsFileService _service = new();

    public HostsViewModel() : base("hosts") { }

    public ObservableCollection<HostsRow> Entries { get; } = new();

    [ObservableProperty] private string _filePath = "—";
    [ObservableProperty] private bool _canWrite;
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _notice = string.Empty;
    [ObservableProperty] private string _editText = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Entries.Clear();
        EditText = string.Empty;
        Notice = string.Empty;
        Summary = "—";
        CanWrite = false;

        HostsFileInfo info;
        try
        {
            info = await _service.GetInfoAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notice = $"读取 hosts 信息失败：{ex.Message}";
            Summary = "读取失败";
            LogError(Notice);
            return;
        }

        FilePath = info.Path;
        CanWrite = info.CanWrite;

        AddRow("文件路径", info.Path);
        AddRow("文件状态", info.Exists ? "存在" : "不存在");
        AddRow("写权限", info.CanWrite ? "可写" : "只读（需要管理员权限）");
        AddRow("条目数", info.EntryCount.ToString());

        if (!info.CanWrite)
        {
            Notice = "需要管理员权限：当前会话只能只读查看，保存按钮已禁用（以管理员身份重新启动 NetCable 后可编辑）。";
            LogWarn("当前会话对 hosts 文件没有写权限");
        }

        try
        {
            var entries = await _service.ReadAsync(cancellationToken);
            foreach (var entry in entries)
                Entries.Add(new HostsRow(entry));

            EditText = string.Join(Environment.NewLine, entries.Select(e => e.Raw));
            Summary = $"{info.Path} · {entries.Count} 条记录 · {(info.CanWrite ? "可写" : "只读")}";
            LogOk($"读取到 {entries.Count} 条 hosts 记录");

            if (entries.Count == 0)
            {
                const string emptyNote = "hosts 文件当前没有生效的 IP ↔ 主机名映射（默认 hosts 只有注释行）。";
                Notice = string.IsNullOrEmpty(Notice) ? emptyNote : $"{Notice} {emptyNote}";
                LogMuted("hosts 文件仅含注释行，没有生效映射");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notice = $"读取 hosts 内容失败：{ex.Message}";
            Summary = "读取失败";
            LogError(Notice);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanWrite)
        {
            LogWarn("没有写权限，已取消保存（需要以管理员身份运行）");
            return;
        }

        var entries = ParseEntries(EditText);
        if (entries.Count == 0)
        {
            LogWarn("编辑区没有可保存的有效条目（格式：IP 主机名 # 注释）");
            return;
        }

        try
        {
            var backup = await _service.BackupAsync();
            LogOk($"已备份 hosts 文件：{backup}");

            await _service.WriteAsync(entries);
            LogOk($"已保存 {entries.Count} 条记录到 {FilePath}");
            StatusText = "已保存";
            await ReloadAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            LogError($"保存失败（需要管理员权限）：{ex.Message}");
            StatusText = "保存失败";
        }
        catch (Exception ex)
        {
            LogError($"保存失败：{ex.Message}");
            StatusText = "保存失败";
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            var entries = await _service.ReadAsync();
            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(new HostsRow(entry));

            EditText = string.Join(Environment.NewLine, entries.Select(e => e.Raw));
            Summary = $"{FilePath} · {entries.Count} 条记录 · {(CanWrite ? "可写" : "只读")}";
        }
        catch (Exception ex)
        {
            LogWarn($"保存后重新读取失败：{ex.Message}");
        }
    }

    /// <summary>把编辑区文本解析为 hosts 条目（忽略空行与纯注释行）。</summary>
    private static List<HostsEntry> ParseEntries(string text)
    {
        var result = new List<HostsEntry>();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string? comment = null;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                comment = line[(hash + 1)..].Trim();
                line = line[..hash].Trim();
                if (line.Length == 0) continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var ip = parts[0];
            for (var i = 1; i < parts.Length; i++)
                result.Add(new HostsEntry(ip, parts[i],
                    string.IsNullOrWhiteSpace(comment) ? null : comment, index + 1));
        }

        return result;
    }
}
