using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Services.Lookup;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>SNTP 时间同步：服务器时间、本机偏差与往返延迟。</summary>
[ToolPage("sntp")]
public sealed partial class SntpViewModel : ToolViewModelBase
{
    private readonly SntpService _service = new();

    public SntpViewModel() : base("sntp") { }

    [ObservableProperty] private string _server = "pool.ntp.org";
    [ObservableProperty] private int _timeout = 3000;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "—";

        if (string.IsNullOrWhiteSpace(Server))
        {
            LogError("请输入 NTP 服务器");
            return;
        }

        LogInfo($"SNTP {Server.Trim()}（超时 {Timeout}ms）");

        var result = await _service.QueryAsync(Server.Trim(), Timeout, cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            LogError(result.Error);
            Summary = $"查询失败：{result.Error}";
            AddRow("状态", "同步失败", result.Error);
            return;
        }

        var localTime = DateTimeOffset.Now;
        AddRow("服务器", result.Server);
        AddRow("服务器时间", result.ServerTime.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        AddRow("本机时间", localTime.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        AddRow("本机偏差", FormatOffset(result.Offset));
        AddRow("往返延迟", $"{result.RoundTrip.TotalMilliseconds:F1} ms");

        Summary = $"偏差 {FormatOffset(result.Offset)} · 往返 {result.RoundTrip.TotalMilliseconds:F1} ms";

        if (Math.Abs(result.Offset.TotalMilliseconds) < 1000)
            LogOk($"时间同步正常（偏差 {FormatOffset(result.Offset)}）");
        else
            LogWarn($"本机时间与服务器相差 {FormatOffset(result.Offset)}，建议校准系统时钟");
    }

    private static string FormatOffset(TimeSpan offset) => $"{offset.TotalMilliseconds:+0.0;-0.0;0.0} ms";
}
