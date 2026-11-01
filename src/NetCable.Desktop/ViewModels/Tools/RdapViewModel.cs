using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Services.Lookup;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>RDAP 查询：域名或 IP，结构化字段表格 + 原始 JSON。</summary>
[ToolPage("rdap")]
public sealed partial class RdapViewModel : ToolViewModelBase
{
    private readonly RdapService _service = new();

    public RdapViewModel() : base("rdap") { }

    [ObservableProperty] private string _query = "example.com";
    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _rawJson = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "—";
        RawJson = string.Empty;

        if (string.IsNullOrWhiteSpace(Query))
        {
            LogError("请输入域名或 IP 地址");
            return;
        }

        LogInfo($"RDAP {Query.Trim()}");

        var result = await _service.QueryAsync(Query.Trim(), cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            LogError(result.Error);
            RawJson = result.RawJson ?? string.Empty;
            Summary = $"查询失败：{result.Error}";
            AddRow("状态", "查询失败", result.Error);
            return;
        }

        foreach (var field in result.Fields)
            AddRow(field.Key, field.Value);

        RawJson = result.RawJson;
        Summary = $"{result.Query} · Handle {result.Handle ?? "—"} · 名称 {result.Name ?? "—"} · " +
                  $"{result.Fields.Count} 个字段 · JSON {result.RawJson.Length:N0} 字符";
        LogOk($"解析出 {result.Fields.Count} 个字段，原始 JSON {result.RawJson.Length:N0} 字符");
    }
}
