using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Models;
using NetCable.Services.Lookup;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>DNS 查询：支持 A/AAAA/CNAME/MX/NS/TXT/SOA/PTR/SRV/CAA，可指定 DNS 服务器。</summary>
[ToolPage("dns")]
public sealed partial class DnsViewModel : ToolViewModelBase
{
    private readonly DnsLookupService _service = new();

    public DnsViewModel() : base("dns") { }

    /// <summary>下拉框可选的记录类型（显示文本）。</summary>
    public IReadOnlyList<string> RecordTypes { get; } =
        new[] { "A", "AAAA", "CNAME", "MX", "NS", "TXT", "SOA", "PTR", "SRV", "CAA" };

    [ObservableProperty] private string _name = "www.bing.com";
    [ObservableProperty] private string _selectedRecordType = "A";
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private int _timeout = 3000;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "—";

        if (string.IsNullOrWhiteSpace(Name))
        {
            LogError("请输入要查询的域名或 IP");
            return;
        }

        var type = ToRecordType(SelectedRecordType);
        var server = string.IsNullOrWhiteSpace(Server) ? null : Server.Trim();
        LogInfo($"查询 {Name.Trim()} · {SelectedRecordType} · 服务器 {server ?? "系统默认"}");

        var result = await _service.QueryAsync(
            new DnsQueryOptions
            {
                Name = Name.Trim(),
                Type = type,
                Server = server,
                TimeoutMs = Timeout
            },
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            LogError(result.Error);
            Summary = $"查询失败：{result.Error}";
            return;
        }

        foreach (var record in result.Records)
            AddRow(record.Name, record.Value, $"{record.Type} · TTL {record.Ttl}");

        Summary = $"{result.Name} · 服务器 {result.Server} · {result.Records.Count} 条记录 · " +
                  $"耗时 {result.Elapsed.TotalMilliseconds:F0} ms";

        if (result.Records.Count == 0)
            LogWarn("查询成功，但没有返回任何记录");
        else
            LogOk($"返回 {result.Records.Count} 条 {SelectedRecordType} 记录，耗时 {result.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static DnsRecordType ToRecordType(string label) => label switch
    {
        "AAAA" => DnsRecordType.Aaaa,
        "CNAME" => DnsRecordType.Cname,
        "MX" => DnsRecordType.Mx,
        "NS" => DnsRecordType.Ns,
        "TXT" => DnsRecordType.Txt,
        "SOA" => DnsRecordType.Soa,
        "PTR" => DnsRecordType.Ptr,
        "SRV" => DnsRecordType.Srv,
        "CAA" => DnsRecordType.Caa,
        _ => DnsRecordType.A
    };
}
