using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Services.Lookup;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>Whois 查询：域名或 IP，解析字段表格 + 原始响应文本。</summary>
[ToolPage("whois")]
public sealed partial class WhoisViewModel : ToolViewModelBase
{
    private readonly WhoisService _service = new();

    public WhoisViewModel() : base("whois") { }

    [ObservableProperty] private string _query = "example.com";
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private int _timeout = 5000;
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _raw = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "—";
        Raw = string.Empty;

        if (string.IsNullOrWhiteSpace(Query))
        {
            LogError("请输入域名或 IP 地址");
            return;
        }

        var server = string.IsNullOrWhiteSpace(Server) ? null : Server.Trim();
        LogInfo($"WHOIS {Query.Trim()} · 服务器 {server ?? "自动跟随 IANA referral"}");

        var result = await _service.QueryAsync(
            new WhoisOptions { Query = Query.Trim(), Server = server, TimeoutMs = Timeout },
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            LogError(result.Error);
            Raw = result.Raw ?? string.Empty;
            Summary = $"查询失败：{result.Error}";
            return;
        }

        foreach (var field in result.Fields)
            AddRow(field.Key, field.Value);

        Raw = result.Raw;
        Summary = $"{result.Query} · 服务器 {result.Server} · {result.Fields.Count} 个字段 · 原文 {result.Raw.Length:N0} 字符";
        LogOk($"收到 {result.Raw.Length:N0} 字符响应，解析出 {result.Fields.Count} 个字段");
    }
}
