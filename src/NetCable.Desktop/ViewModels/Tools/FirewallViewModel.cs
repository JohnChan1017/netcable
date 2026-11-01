using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>防火墙规则表的一行。</summary>
public sealed class FirewallRow
{
    public FirewallRow(FirewallRule rule)
    {
        Name = rule.Name;
        Direction = rule.Direction;
        Action = rule.Action;
        Protocol = rule.Protocol ?? "—";
        LocalPort = rule.LocalPort ?? "—";
        RemoteAddress = rule.RemoteAddress ?? "—";
        Enabled = rule.Enabled ? "是" : "否";
    }

    public string Name { get; }
    public string Direction { get; }
    public string Action { get; }
    public string Protocol { get; }
    public string LocalPort { get; }
    public string RemoteAddress { get; }
    public string Enabled { get; }
}

/// <summary>防火墙（只读）：状态卡 + 规则表。权限不足/平台不支持时给出明确原因。</summary>
[ToolPage("firewall")]
public sealed partial class FirewallViewModel : ToolViewModelBase
{
    private readonly FirewallService _service = new();

    public FirewallViewModel() : base("firewall") { }

    public ObservableCollection<FirewallRow> Rules { get; } = new();

    [ObservableProperty] private string _statusLine = "—";
    [ObservableProperty] private string _notice = string.Empty;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Rules.Clear();
        Notice = string.Empty;
        Summary = "读取中…";
        StatusLine = "—";

        FirewallStatus status;
        try
        {
            status = await _service.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notice = $"读取防火墙状态失败：{ex.Message}";
            Summary = "读取失败";
            LogError(Notice);
            return;
        }

        var enabledText = status.Enabled switch { true => "已启用", false => "已禁用", _ => "未知" };
        StatusLine = string.Join(" · ", new[]
        {
            status.Engine ?? "未知引擎",
            enabledText,
            status.Profile
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        AddRow("引擎", status.Engine ?? "未知");
        AddRow("支持", status.Supported ? "是" : "否", status.Supported ? null : status.UnsupportedReason);
        AddRow("启用状态", enabledText);
        AddRow("配置文件", status.Profile ?? "—");

        if (!status.Supported)
        {
            Notice = status.UnsupportedReason ?? "当前平台或权限不支持读取防火墙信息";
            Summary = "不可用";
            LogWarn(Notice);
            return;
        }

        LogInfo($"防火墙状态：{StatusLine}");

        try
        {
            var rules = await _service.GetRulesAsync(cancellationToken);
            foreach (var rule in rules)
                Rules.Add(new FirewallRow(rule));

            Summary = $"{StatusLine} · {rules.Count} 条规则";
            LogOk($"读取到 {rules.Count} 条防火墙规则");
            if (rules.Count == 0)
                LogMuted("没有返回任何规则（可能权限不足、规则为空或该防火墙不使用规则表）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Summary = $"{StatusLine} · 规则读取失败";
            Notice = $"规则读取失败：{ex.Message}";
            LogWarn(Notice);
        }
    }
}
