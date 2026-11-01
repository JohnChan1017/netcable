using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Lookup;

/// <summary>
/// HTTP / TLS 探测服务。手动跟随重定向以记录完整跳转链，读取状态码、HTTP 版本、
/// 响应头与耗时；再用 SslStream 单独握手取证书 Subject/Issuer/有效期/指纹/SAN/协议。
/// 任何失败都写入 Error，不抛异常。
/// </summary>
public sealed class HttpTlsService : IHttpTlsService
{
    private const int TimeoutMs = 15000;
    private const int TlsTimeoutMs = 10000;
    private const int MaxRedirects = 10;
    private const int MaxHeaders = 100;

    private readonly HttpClient _http;

    /// <summary>使用内置 HttpClient（禁用自动重定向，以便记录跳转链）。</summary>
    public HttpTlsService() : this(null) { }

    /// <summary>可选注入 HttpClient；若注入的实例开启自动重定向，则 Redirects 会为空。</summary>
    public HttpTlsService(HttpClient? httpClient)
    {
        _http = httpClient ?? LookupHttp.CreateClient(allowAutoRedirect: false, timeout: Timeout.InfiniteTimeSpan);
    }

    /// <summary>探测指定 URL 的 HTTP 响应与 TLS 证书信息。</summary>
    public async Task<HttpTlsInfo> InspectAsync(string url, CancellationToken cancellationToken = default)
    {
        var input = url?.Trim() ?? string.Empty;
        if (input.Length == 0) return Failure(input, "URL 为空");
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Failure(input, "URL 格式无效（仅支持 http/https）");
        }

        var redirects = new List<string>();
        var headers = new List<HttpTlsHeader>();
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        var current = uri;

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, TimeoutMs);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("*/*");

                var currentResponse = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                var status = (int)currentResponse.StatusCode;
                if (IsRedirect(status) && currentResponse.Headers.Location is not null && hop < MaxRedirects)
                {
                    var location = currentResponse.Headers.Location;
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    currentResponse.Dispose();
                    redirects.Add(next.ToString());
                    current = next;
                    continue;
                }

                response = currentResponse;
                break;
            }

            if (response is null)
            {
                stopwatch.Stop();
                return Failure(input, "重定向次数过多", redirects);
            }

            using var finalResponse = response;
            stopwatch.Stop();

            foreach (var header in finalResponse.Headers)
            {
                if (headers.Count >= MaxHeaders) break;
                headers.Add(new HttpTlsHeader(header.Key, string.Join(", ", header.Value)));
            }
            foreach (var header in finalResponse.Content.Headers)
            {
                if (headers.Count >= MaxHeaders) break;
                headers.Add(new HttpTlsHeader(header.Key, string.Join(", ", header.Value)));
            }

            TlsCertificateInfo? certificate = null;
            string? tlsError = null;
            if (current.Scheme == Uri.UriSchemeHttps)
            {
                try
                {
                    var port = current.IsDefaultPort ? 443 : current.Port;
                    certificate = await GetCertificateAsync(current.Host, port, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    tlsError = "TLS 证书获取失败：" + LookupErrors.Describe(ex, TlsTimeoutMs);
                }
            }

            return new HttpTlsInfo(
                Url: input,
                FinalUrl: current.ToString(),
                StatusCode: (int)finalResponse.StatusCode,
                StatusText: finalResponse.ReasonPhrase,
                HttpVersion: finalResponse.Version.ToString(),
                Server: finalResponse.Headers.Server?.ToString(),
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                Headers: headers,
                Certificate: certificate,
                Redirects: redirects,
                Error: tlsError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            stopwatch.Stop();
            return Failure(input, $"HTTP 请求超时（{TimeoutMs} ms）", redirects);
        }
        catch (Exception ex)
        {
            response?.Dispose();
            stopwatch.Stop();
            return Failure(input, LookupErrors.Describe(ex, TimeoutMs), redirects);
        }
    }

    private static bool IsRedirect(int statusCode) => statusCode is 301 or 302 or 303 or 307 or 308;

    private static async Task<TlsCertificateInfo> GetCertificateAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, TlsTimeoutMs);
        await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        client.NoDelay = true;

        X509Certificate2? captured = null;
        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.None,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http11,
                SslApplicationProtocol.Http2
            },
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is not null) captured = new X509Certificate2(certificate);
                return true; // 只观察证书，不因其不受信而失败
            }
        };

        await ssl.AuthenticateAsClientAsync(options, cts.Token).ConfigureAwait(false);
        if (captured is null) throw new InvalidOperationException("服务器未提供 TLS 证书");

        using (captured)
        {
            var thumbprint = captured.Thumbprint;
            if (string.IsNullOrEmpty(thumbprint))
                thumbprint = Convert.ToHexString(SHA1.HashData(captured.RawData));

            var dnsNames = new List<string>();
            var subjectAlternativeName = captured.Extensions
                .OfType<X509SubjectAlternativeNameExtension>()
                .FirstOrDefault();
            if (subjectAlternativeName is not null)
            {
                foreach (var name in subjectAlternativeName.EnumerateDnsNames())
                {
                    if (!string.IsNullOrWhiteSpace(name)) dnsNames.Add(name.Trim());
                }
            }

            var protocol = ssl.SslProtocol == SslProtocols.None ? null : ssl.SslProtocol.ToString();
            return new TlsCertificateInfo(
                Subject: captured.Subject,
                Issuer: captured.Issuer,
                NotBefore: new DateTimeOffset(captured.NotBefore),
                NotAfter: new DateTimeOffset(captured.NotAfter),
                Thumbprint: thumbprint,
                DnsNames: dnsNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Protocol: protocol);
        }
    }

    private static HttpTlsInfo Failure(
        string url,
        string error,
        IReadOnlyList<string>? redirects = null) => new(
        Url: url,
        FinalUrl: null,
        StatusCode: null,
        StatusText: null,
        HttpVersion: null,
        Server: null,
        ElapsedMs: 0,
        Headers: Array.Empty<HttpTlsHeader>(),
        Certificate: null,
        Redirects: redirects ?? Array.Empty<string>(),
        Error: error);
}
