using CommunityToolkit.Mvvm.ComponentModel;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Services.Lookup;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>HTTP / TLS：状态码、重定向链、响应头与证书信息（含到期天数）。</summary>
[ToolPage("httptls")]
public sealed partial class HttpTlsViewModel : ToolViewModelBase
{
    private readonly IHttpTlsService _service = new HttpTlsService();

    public HttpTlsViewModel() : base("httptls")
    {
        Summary = "输入 URL 后点击「探测」，查看 HTTP 响应与 TLS 证书";
    }

    [ObservableProperty] private string _url = "https://www.bing.com";
    [ObservableProperty] private string _summary = "—";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "探测中…";

        var url = Url.Trim();
        if (url.Length == 0)
        {
            LogError("请输入 URL，例如 https://www.bing.com");
            Summary = "—";
            return;
        }

        LogInfo($"探测 {url}（HTTP 状态 / 重定向 / 响应头 / TLS 证书）");
        var info = await _service.InspectAsync(url, cancellationToken).ConfigureAwait(true);

        AddRow("请求 URL", info.Url);
        AddRow("最终 URL", info.FinalUrl ?? "—");
        AddRow("状态码", info.StatusCode is { } status
                ? $"{status} {info.StatusText}".TrimEnd()
                : "—",
            info.HttpVersion ?? "—");
        AddRow("耗时", $"{info.ElapsedMs} ms");
        AddRow("服务器", info.Server ?? "—");
        AddRow("重定向链", info.Redirects.Count == 0 ? "无（0 跳）" : $"{info.Redirects.Count} 跳",
            info.Redirects.Count == 0 ? "直接返回" : string.Join(" → ", info.Redirects));

        if (!string.IsNullOrWhiteSpace(info.Error))
        {
            AddRow("错误", info.Error, "详见日志");
            AddRow("环境提示", "本机 HTTPS 证书链 / SChannel 不可用",
                "可改用 http:// 地址验证 HTTP 部分；TLS 证书信息需要可用的 HTTPS 连接");
            Summary = "探测失败：" + info.Error;
            LogError(Summary);
            LogWarn("HTTPS 请求失败：本机 SChannel / 证书链环境受限时属预期现象（http:// 探测通常仍可用）。");
        }
        else if (info.StatusCode is not null)
        {
            Summary = $"{info.StatusCode} {info.StatusText} · {info.HttpVersion} · {info.ElapsedMs} ms";
            LogOk($"HTTP 响应：{Summary}");
        }

        AddCertificateRows(info);

        AddRow("响应头", $"{info.Headers.Count} 项");
        foreach (var header in info.Headers)
            AddRow("  " + header.Name, header.Value);

        if (info.Redirects.Count > 0)
        {
            for (var i = 0; i < info.Redirects.Count; i++)
                LogInfo($"重定向 {i + 1}: {info.Redirects[i]}");
        }
    }

    private void AddCertificateRows(HttpTlsInfo info)
    {
        var certificate = info.Certificate;
        if (certificate is null)
        {
            AddRow("TLS 证书", "未获取",
                info.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "见错误信息" : "非 HTTPS 请求");
            return;
        }

        var daysLeft = (certificate.NotAfter - DateTimeOffset.Now).Days;
        AddRow("证书 Subject", certificate.Subject);
        AddRow("证书 Issuer", certificate.Issuer);
        AddRow("有效期", $"{certificate.NotBefore:yyyy-MM-dd HH:mm} — {certificate.NotAfter:yyyy-MM-dd HH:mm}",
            daysLeft < 0 ? "已过期" : $"剩余 {daysLeft} 天");
        AddRow("TLS 协议", certificate.Protocol ?? "—");
        AddRow("证书指纹", certificate.Thumbprint ?? "—");
        AddRow("SAN 域名", certificate.DnsNames.Count == 0 ? "—" : $"{certificate.DnsNames.Count} 个",
            string.Join(" · ", certificate.DnsNames));

        if (daysLeft is >= 0 and <= 30) LogWarn($"证书将在 {daysLeft} 天后过期");
        else if (daysLeft < 0) LogError("证书已过期");
        else LogInfo($"TLS 证书：{certificate.Protocol ?? "—"}，剩余 {daysLeft} 天");
    }
}
