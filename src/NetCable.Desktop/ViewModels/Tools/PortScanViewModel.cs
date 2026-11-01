using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>端口扫描：TCP 探测指定主机的端口段，只把开放端口写入结果表。</summary>
[ToolPage("portscan")]
public sealed partial class PortScanViewModel : ToolViewModelBase
{
    private readonly IPortScannerService _scanner = new PortScannerService();

    public PortScanViewModel() : base("portscan")
    {
        Summary = "支持 1-1024 端口段与 22,80,443 列表写法";
    }

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _portRange = "1-1024";
    [ObservableProperty] private int _concurrency = 256;
    [ObservableProperty] private int _timeout = 1000;
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "扫描中…";

        var host = Host.Trim();
        if (host.Length == 0)
        {
            LogError("请输入目标主机");
            Summary = "—";
            return;
        }

        if (!TryParsePorts(PortRange, out var ports, out var error))
        {
            LogError(error!);
            Summary = "—";
            return;
        }

        var options = new PortScanOptions
        {
            Host = host,
            Ports = ports,
            StartPort = ports.Min(),
            EndPort = ports.Max(),
            TimeoutMs = Math.Clamp(Timeout, 100, 10000),
            Concurrency = Math.Clamp(Concurrency, 1, 1024),
            ResolveService = true
        };

        LogInfo($"扫描 {options.Host} 的 {ports.Length:N0} 个端口" +
                $"（{ports.Min()}–{ports.Max()}，并发 {options.Concurrency}，超时 {options.TimeoutMs}ms）");

        var openCount = 0;
        var started = DateTimeOffset.Now;

        await foreach (var result in _scanner.ScanAsync(options, cancellationToken).ConfigureAwait(true))
        {
            if (!result.Open) continue;

            openCount++;
            var service = string.IsNullOrWhiteSpace(result.Service) ? "未知服务" : result.Service!;
            var latency = result.LatencyMs is { } ms ? $"{ms} ms" : "—";
            var banner = NormalizeBanner(result.Banner);
            var extra = banner.Length == 0 ? latency : $"{latency}  ·  {banner}";

            AddRow(result.Port.ToString(CultureInfo.InvariantCulture), service, extra);
            LogOk($"{result.Port,5}/tcp  开放  {service,-16}  {latency}{(banner.Length == 0 ? "" : "  " + banner)}");
            StatusText = $"已发现 {openCount} 个开放端口…";
        }

        var elapsed = DateTimeOffset.Now - started;
        if (openCount == 0)
        {
            Summary = $"扫描 {ports.Length:N0} 个端口，未发现开放端口（{elapsed.TotalSeconds:F1}s）";
            LogWarn("未发现开放端口：目标可能全部关闭，或防火墙过滤了探测报文");
        }
        else
        {
            Summary = $"扫描 {ports.Length:N0} 个端口，开放 {openCount} 个（{elapsed.TotalSeconds:F1}s）";
            LogOk(Summary);
        }
    }

    private static string NormalizeBanner(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return string.Empty;
        var text = banner.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= 100 ? text : text[..100] + "…";
    }

    /// <summary>解析 <c>1-1024</c>、<c>22,80,443</c>、<c>1-1024,3389,8080</c> 等写法。</summary>
    internal static bool TryParsePorts(string text, out int[] ports, out string? error)
    {
        ports = Array.Empty<int>();
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入端口段，例如 1-1024 或 22,80,443";
            return false;
        }

        var set = new SortedSet<int>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single) ||
                    single is < 1 or > 65535)
                {
                    error = $"端口无效：{token}（有效范围 1–65535）";
                    return false;
                }

                set.Add(single);
                continue;
            }

            var startText = token[..dash].Trim();
            var endText = token[(dash + 1)..].Trim();
            if (!int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                !int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
                start is < 1 or > 65535 || end is < 1 or > 65535)
            {
                error = $"端口段无效：{token}（有效范围 1–65535）";
                return false;
            }

            if (start > end) (start, end) = (end, start);

            for (var port = start; port <= end; port++)
            {
                set.Add(port);
                if (set.Count > 20000)
                {
                    error = "端口数量过多（一次最多 20000 个）";
                    return false;
                }
            }
        }

        if (set.Count == 0)
        {
            error = "未能解析出任何端口";
            return false;
        }

        ports = set.ToArray();
        return true;
    }
}
