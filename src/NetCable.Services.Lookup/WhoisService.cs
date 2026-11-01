using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Lookup;

/// <summary>
/// WHOIS 查询服务：TCP 43 直连。域名先查 whois.iana.org 并跟随 refer/whois 字段；
/// 无法从 IANA 得到服务器时使用内置 WhoisServers.xml 的 TLD→服务器映射；
/// IP 查询先经 IANA 定位 RIR，失败时回退 ARIN。结果解析为 key-value 字段并保留原始文本。
/// </summary>
public sealed partial class WhoisService : IWhoisService
{
    private const int MaxResponseBytes = 512 * 1024;
    private const int MaxHops = 3;
    private const string IanaServer = "whois.iana.org";
    private const string ArinServer = "whois.arin.net";

    private static readonly Lazy<Dictionary<string, string>> TldServers =
        new(LoadTldServers, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly string[] BoilerplatePrefixes =
    {
        "notice", "terms of use", "url of the icann", "for more information on whois",
        "icann whois", "last update of whois", "whois inaccuracy", "the data contained",
        "by using", "this data"
    };

    [GeneratedRegex(@"(?im)^\s*(?:refer|whois|referralserver|whois-server)\s*:\s*(?:whois://)?\s*(?<host>[a-z0-9][a-z0-9.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferRegex();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z0-9 .,/_()\-]{0,60}?)\s*:\s*(?<value>.*)$")]
    private static partial Regex FieldRegex();

    /// <summary>执行一次 WHOIS 查询。连接超时、服务器无响应或解析失败都会写入 Error，不抛异常。</summary>
    public async Task<WhoisResult> QueryAsync(WhoisOptions options, CancellationToken cancellationToken = default)
    {
        var query = options.Query?.Trim() ?? string.Empty;
        var timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : 10000;
        var explicitServer = string.IsNullOrWhiteSpace(options.Server) ? null : options.Server.Trim();

        if (query.Length == 0)
            return new WhoisResult(query, explicitServer ?? string.Empty,
                Array.Empty<KeyValuePair<string, string>>(), string.Empty, "查询内容为空");

        if (explicitServer is not null)
        {
            var (text, error) = await QueryServerAsync(explicitServer, query, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            return BuildResult(query, NormalizeServerName(explicitServer) ?? explicitServer, text, error);
        }

        var isIp = IPAddress.TryParse(query, out _);
        return isIp
            ? await QueryIpAsync(query, timeoutMs, cancellationToken).ConfigureAwait(false)
            : await QueryDomainAsync(query, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WhoisResult> QueryDomainAsync(string query, int timeoutMs, CancellationToken cancellationToken)
    {
        var domain = query.TrimEnd('.');
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IanaServer };
        string? server = IanaServer;
        string? text = null;
        string? lastError = null;

        // 1) 先问 IANA，跟随 refer/whois 字段（IANA 对部分域名只返回自身对象，需继续走 TLD 映射）
        var (ianaText, ianaError) = await QueryServerAsync(IanaServer, domain, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (ianaText is not null)
        {
            var refer = ExtractRefer(ianaText, IanaServer);
            if (refer is not null)
            {
                var (referredText, referredServer) =
                    await FollowReferralsAsync(refer, domain, timeoutMs, visited, cancellationToken).ConfigureAwait(false);
                if (referredText is not null)
                {
                    text = referredText;
                    server = referredServer ?? refer;
                }
                else
                {
                    lastError = $"无法从 {refer}:43 获取 WHOIS 响应";
                }
            }
        }
        else
        {
            lastError = ianaError;
        }

        // 2) IANA 无 refer 时使用内置 TLD → 服务器映射
        var tld = domain.Contains('.') ? domain[(domain.LastIndexOf('.') + 1)..] : domain;
        if (text is null && TldServers.Value.TryGetValue(tld, out var tldServer) &&
            !string.IsNullOrWhiteSpace(tldServer))
        {
            var (mapText, mapError) = await QueryServerAsync(tldServer, domain, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (mapText is not null)
            {
                text = mapText;
                server = tldServer;
                visited.Add(tldServer);
                var refer = ExtractRefer(mapText, tldServer);
                if (refer is not null)
                {
                    var (referredText, referredServer) =
                        await FollowReferralsAsync(refer, domain, timeoutMs, visited, cancellationToken).ConfigureAwait(false);
                    if (referredText is not null)
                    {
                        text = referredText;
                        server = referredServer ?? refer;
                    }
                }
            }
            else
            {
                lastError = mapError;
            }
        }

        // 3) 映射里没有该 TLD（新顶级域）时再向 IANA 查 TLD 并跟随
        if (text is null)
        {
            var (tldText, tldError) = await QueryServerAsync(IanaServer, tld, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (tldText is not null)
            {
                var tldRefer = ExtractRefer(tldText, IanaServer);
                if (tldRefer is not null)
                {
                    var (referredText, referredServer) =
                        await FollowReferralsAsync(tldRefer, domain, timeoutMs, visited, cancellationToken).ConfigureAwait(false);
                    if (referredText is not null)
                    {
                        text = referredText;
                        server = referredServer ?? tldRefer;
                        lastError = null;
                    }
                    else
                    {
                        text = tldText;
                    }
                }
                else
                {
                    text = tldText;
                }
            }
            else
            {
                lastError ??= tldError;
            }
        }

        // 4) 最后退回 IANA 返回的域对象，避免整次查询完全失败
        if (text is null && ianaText is not null)
        {
            text = ianaText;
            server = IanaServer;
        }

        return BuildResult(query, server ?? IanaServer, text,
            text is null ? lastError ?? "无法确定该域名的 WHOIS 服务器" : null);
    }

    private async Task<WhoisResult> QueryIpAsync(string query, int timeoutMs, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastError = null;
        string server = IanaServer;

        foreach (var bootstrap in new[] { IanaServer, ArinServer })
        {
            if (!visited.Add(bootstrap)) continue;
            server = bootstrap;
            var (text, error) = await QueryServerAsync(bootstrap, query, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (text is null)
            {
                lastError = error;
                continue;
            }

            var refer = ExtractRefer(text, bootstrap);
            if (refer is not null && visited.Add(refer))
            {
                server = NormalizeServerName(refer) ?? refer;
                var (referredText, referredError) = await QueryServerAsync(server, query, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (referredText is not null) text = referredText;
                else lastError = referredError;
            }

            return BuildResult(query, server, text, null);
        }

        return BuildResult(query, server, null, lastError ?? "无法定位该 IP 的 WHOIS 服务器");
    }

    private async Task<(string? Text, string? Server)> FollowReferralsAsync(
        string firstReferral,
        string query,
        int timeoutMs,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        string? referral = firstReferral;
        string? text = null;
        string? server = null;

        for (var hop = 0; referral is not null && hop < MaxHops; hop++)
        {
            var host = NormalizeServerName(referral);
            if (host is null || !visited.Add(host)) break;
            server = host;
            var (nextText, _) = await QueryServerAsync(host, query, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (nextText is null) break;
            text = nextText;
            referral = ExtractRefer(nextText, host);
        }

        return (text, server);
    }

    private static string? ExtractRefer(string raw, string currentServer)
    {
        foreach (Match match in ReferRegex().Matches(raw))
        {
            var host = NormalizeServerName(match.Groups["host"].Value);
            if (string.IsNullOrWhiteSpace(host)) continue;
            if (host.Equals(currentServer, StringComparison.OrdinalIgnoreCase)) continue;
            if (!host.Contains('.')) continue;
            return host;
        }
        return null;
    }

    private static string? NormalizeServerName(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;
        var host = server.Trim();
        if (host.StartsWith("whois://", StringComparison.OrdinalIgnoreCase))
            host = host["whois://".Length..];
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        host = host.Trim().TrimEnd('.');
        return host.Length > 0 ? host.ToLowerInvariant() : null;
    }

    private static async Task<(string? Text, string? Error)> QueryServerAsync(
        string server,
        string query,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var host = NormalizeServerName(server);
        if (host is null) return (null, $"WHOIS 服务器地址无效：{server}");

        using var client = new TcpClient();
        using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, timeoutMs);
        try
        {
            await client.ConnectAsync(host, 43, cts.Token).ConfigureAwait(false);
            client.NoDelay = true;
            await using var stream = client.GetStream();

            var payload = Encoding.ASCII.GetBytes(query + "\r\n");
            await stream.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (buffer.Length < MaxResponseBytes)
            {
                var read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
            }

            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
            if (text.Length == 0) return (null, $"{host}:43 返回空响应");
            return (text, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, $"连接 {host}:43 超时（{timeoutMs} ms）");
        }
        catch (Exception ex)
        {
            return (null, $"{host}:43 {LookupErrors.Describe(ex, timeoutMs)}");
        }
    }

    private static WhoisResult BuildResult(string query, string server, string? raw, string? error)
    {
        if (raw is null)
        {
            return new WhoisResult(
                query,
                NormalizeServerName(server) ?? server,
                Array.Empty<KeyValuePair<string, string>>(),
                string.Empty,
                error ?? "WHOIS 查询失败");
        }

        return new WhoisResult(
            query,
            NormalizeServerName(server) ?? server,
            ParseFields(raw),
            raw,
            null);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseFields(string raw)
    {
        var fields = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed[0] is '%' or '#' or '>' or '=' or '-') continue;

            var match = FieldRegex().Match(line);
            if (!match.Success) continue;

            var key = Regex.Replace(match.Groups["key"].Value.Trim(), @"\s+", " ");
            if (key.Length == 0) continue;
            if (BoilerplatePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;

            var value = match.Groups["value"].Value.Trim();
            if (value.Length == 0) continue;

            value = LookupErrors.Truncate(value, 500);
            var identity = key + "\u0000" + value;
            if (seen.Add(identity)) fields.Add(new KeyValuePair<string, string>(key, value));
        }

        return fields;
    }

    private static Dictionary<string, string> LoadTldServers()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(WhoisResult).Assembly
                .GetManifestResourceStream("NetCable.Core.Resources.WhoisServers.xml");
            if (stream is null) return map;

            var document = XDocument.Load(stream);
            foreach (var node in document.Root?.Elements("WhoisServer") ?? Enumerable.Empty<XElement>())
            {
                var tld = node.Element("TLD")?.Value?.Trim();
                var server = node.Element("Server")?.Value?.Trim();
                if (!string.IsNullOrEmpty(tld) && !string.IsNullOrEmpty(server))
                    map[tld] = server;
            }
        }
        catch
        {
            // 内嵌资源缺失或损坏时退化为仅 IANA 查询
        }

        return map;
    }
}
