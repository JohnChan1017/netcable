using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NetCable.Services.Lookup;

/// <summary>
/// Lookup 服务共用的 HTTP 客户端工厂。默认启用系统代理（HTTPS_PROXY 等环境变量）、
/// 自动解压与连接超时，请求级超时由各服务通过 CancellationTokenSource 控制。
/// </summary>
internal static class LookupHttp
{
    private const string UserAgent = "NetCable/1.0 (Lookup)";

    /// <summary>创建一个带连接超时与自动解压的 HttpClient。默认不设置整体超时，由调用方按请求控制。</summary>
    public static HttpClient CreateClient(bool allowAutoRedirect = true, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            MaxConnectionsPerServer = 8
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>创建与外部取消令牌联动、并在指定毫秒后自动取消的令牌源。</summary>
    public static CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken, int timeoutMs)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0 && timeoutMs != Timeout.Infinite) cts.CancelAfter(timeoutMs);
        return cts;
    }
}

/// <summary>把常见网络异常转换为中文提示，避免异常直接冒泡到 UI。</summary>
internal static class LookupErrors
{
    /// <summary>生成面向用户的错误说明；明确的外部取消由调用方继续抛出，不在本方法处理。</summary>
    public static string Describe(Exception exception, int timeoutMs)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return $"请求超时（{timeoutMs} ms）";
            case SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData:
                return $"无法解析目标主机：{socket.Message}";
            case SocketException socket:
                return $"网络连接失败（{socket.SocketErrorCode}）：{socket.Message}";
            case HttpRequestException http when http.HttpRequestError == HttpRequestError.NameResolutionError:
                return $"无法解析目标主机：{http.Message}";
            case HttpRequestException http when http.HttpRequestError == HttpRequestError.SecureConnectionError:
                return $"TLS 连接失败：{http.Message}";
            case HttpRequestException http when http.HttpRequestError == HttpRequestError.ConnectionError:
                return $"无法连接到服务器：{http.Message}";
            case HttpRequestException http:
                return $"HTTP 请求失败：{http.Message}";
            case System.Text.Json.JsonException json:
                return $"JSON 解析失败：{json.Message}";
            case IOException io:
                return $"网络读写失败：{io.Message}";
            default:
                return exception.Message;
        }
    }

    /// <summary>截断字符串到指定长度，超长时补省略号。</summary>
    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
