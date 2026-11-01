using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Lookup;

/// <summary>
/// RDAP 查询服务（HTTP/JSON）。默认访问 https://rdap.org/ 的公共重定向入口，
/// 按查询内容自动选择 /domain/、/ip/ 或 /autnum/ 路径，并把常见 JSON 字段映射为 key-value。
/// </summary>
public sealed class RdapService : IRdapService
{
    private const int TimeoutMs = 15000;
    private const int MaxRawJsonChars = 256 * 1024;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <summary>使用默认 rdap.org 入口与内置 HttpClient。</summary>
    public RdapService() : this(null) { }

    /// <summary>可选注入 HttpClient（便于测试或改用私有 RDAP 入口）。</summary>
    public RdapService(HttpClient? httpClient) : this(httpClient, "https://rdap.org/") { }

    /// <summary>指定 RDAP 入口地址，未以 / 结尾时会自动补齐。</summary>
    public RdapService(HttpClient? httpClient, string baseUrl)
    {
        _http = httpClient ?? LookupHttp.CreateClient(timeout: Timeout.InfiniteTimeSpan);
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    /// <summary>执行一次 RDAP 查询。HTTP 错误、超时或 JSON 解析失败都会写入 Error，不抛异常。</summary>
    public async Task<RdapResult> QueryAsync(string query, CancellationToken cancellationToken = default)
    {
        var rawQuery = query?.Trim() ?? string.Empty;
        if (rawQuery.Length == 0)
            return ErrorResult(rawQuery, string.Empty, "查询内容为空");

        var normalized = NormalizeQuery(rawQuery);
        var url = BuildUrl(normalized);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, TimeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/rdap+json");
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var rawJson = LookupErrors.Truncate(body, MaxRawJsonChars);

            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode == HttpStatusCode.NotFound
                    ? "RDAP 未找到该对象的记录（HTTP 404）"
                    : $"RDAP 请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return new RdapResult(rawQuery, null, null,
                    Array.Empty<KeyValuePair<string, string>>(), rawJson, reason);
            }

            if (string.IsNullOrWhiteSpace(body))
                return new RdapResult(rawQuery, null, null,
                    Array.Empty<KeyValuePair<string, string>>(), rawJson, "RDAP 返回空响应");

            try
            {
                var (handle, name, fields) = ParseJson(body);
                return new RdapResult(rawQuery, handle, name, fields, rawJson, null);
            }
            catch (JsonException ex)
            {
                return new RdapResult(rawQuery, null, null,
                    Array.Empty<KeyValuePair<string, string>>(), rawJson,
                    $"RDAP JSON 解析失败：{ex.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return ErrorResult(rawQuery, string.Empty, $"RDAP 请求超时（{TimeoutMs} ms）");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ErrorResult(rawQuery, string.Empty, LookupErrors.Describe(ex, TimeoutMs));
        }
    }

    private static RdapResult ErrorResult(string query, string rawJson, string error) =>
        new(query, null, null, Array.Empty<KeyValuePair<string, string>>(), rawJson, error);

    private static string NormalizeQuery(string query)
    {
        var value = query.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            value = value["http://".Length..];
        else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = value["https://".Length..];
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        return value.TrimEnd('.');
    }

    private string BuildUrl(string query)
    {
        if (IPAddress.TryParse(query, out _))
            return _baseUrl + "ip/" + Uri.EscapeDataString(query);

        var upper = query.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? query[2..] : null;
        if (upper is not null && int.TryParse(upper, out _))
            return _baseUrl + "autnum/" + Uri.EscapeDataString(upper);

        return _baseUrl + "domain/" + Uri.EscapeDataString(query);
    }

    private static (string? Handle, string? Name, IReadOnlyList<KeyValuePair<string, string>> Fields) ParseJson(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var fields = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddField(fields, seen, "objectClassName", GetString(root, "objectClassName"));
        AddField(fields, seen, "handle", GetString(root, "handle"));
        AddField(fields, seen, "ldhName", GetString(root, "ldhName"));
        AddField(fields, seen, "unicodeName", GetString(root, "unicodeName"));
        AddField(fields, seen, "port43", GetString(root, "port43"));
        AddField(fields, seen, "parentHandle", GetString(root, "parentHandle"));

        AddField(fields, seen, "status", JoinArray(root, "status"));
        AddField(fields, seen, "notice", JoinArray(root, "rdapConformance"));

        AddStringCollection(fields, seen, root, "nameservers", "nameserver", "ldhName");
        AddStringCollection(fields, seen, root, "entities", "entity", "handle");
        AddEvents(fields, seen, root);
        AddEntities(fields, seen, root);
        AddPublicIds(fields, seen, root);
        AddRemarks(fields, seen, root, "remarks", "remark");
        AddRemarks(fields, seen, root, "notices", "notice");
        AddLinks(fields, seen, root);

        // IP 网络对象
        AddField(fields, seen, "startAddress", GetString(root, "startAddress"));
        AddField(fields, seen, "endAddress", GetString(root, "endAddress"));
        AddField(fields, seen, "ipVersion", GetString(root, "ipVersion"));
        AddField(fields, seen, "type", GetString(root, "type"));
        AddField(fields, seen, "country", GetString(root, "country"));
        AddField(fields, seen, "name", GetString(root, "name"));

        // ASN 对象
        AddField(fields, seen, "startAutnum", GetNumber(root, "startAutnum"));
        AddField(fields, seen, "endAutnum", GetNumber(root, "endAutnum"));

        if (root.TryGetProperty("secureDNS", out var secureDns) && secureDns.ValueKind == JsonValueKind.Object)
        {
            AddField(fields, seen, "zoneSigned", GetString(secureDns, "zoneSigned"));
            AddField(fields, seen, "delegationSigned", GetString(secureDns, "delegationSigned"));
        }

        var handle = GetString(root, "handle");
        var name = GetString(root, "ldhName") ?? GetString(root, "unicodeName") ?? GetString(root, "name");
        return (handle, name, fields);
    }

    private static void AddField(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmedKey = key.Trim();
        var trimmedValue = LookupErrors.Truncate(value.Trim(), 500);
        if (trimmedValue.Length == 0) return;
        var identity = trimmedKey + "\u0000" + trimmedValue;
        if (seen.Add(identity)) fields.Add(new KeyValuePair<string, string>(trimmedKey, trimmedValue));
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.ToString()
            : null;

    private static string? JoinArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var items = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        return items.Count > 0 ? string.Join(", ", items) : null;
    }

    private static void AddStringCollection(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        JsonElement root,
        string collectionName,
        string fieldName,
        string valueName)
    {
        if (!root.TryGetProperty(collectionName, out var collection) ||
            collection.ValueKind != JsonValueKind.Array) return;

        foreach (var item in collection.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var value = GetString(item, valueName);
            if (!string.IsNullOrWhiteSpace(value)) AddField(fields, seen, fieldName, value);
        }
    }

    private static void AddEvents(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        JsonElement root)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return;
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var action = GetString(item, "eventAction");
            var date = GetString(item, "eventDate");
            if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(date)) continue;
            AddField(fields, seen, "event:" + action, date);
        }
    }

    private static void AddEntities(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array) return;
        foreach (var item in entities.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var handle = GetString(item, "handle");
            var name = GetVcardName(item);
            var roles = JoinArray(item, "roles");
            var label = handle ?? name ?? "entity";
            var value = string.IsNullOrWhiteSpace(roles) ? label : $"{label} ({roles})";
            AddField(fields, seen, "entity", value);
        }
    }

    private static string? GetVcardName(JsonElement entity)
    {
        if (!entity.TryGetProperty("vcardArray", out var vcard) ||
            vcard.ValueKind != JsonValueKind.Array) return null;

        var entries = vcard.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Array);
        if (entries.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array) continue;
            var parts = entry.EnumerateArray().ToList();
            if (parts.Count < 4) continue;
            if (parts[0].ValueKind != JsonValueKind.String || parts[0].GetString() != "fn") continue;
            if (parts[3].ValueKind == JsonValueKind.String) return parts[3].GetString();
        }
        return null;
    }

    private static void AddPublicIds(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        JsonElement root)
    {
        if (!root.TryGetProperty("publicIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
        foreach (var item in ids.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(item, "type");
            var identifier = GetString(item, "identifier");
            if (!string.IsNullOrWhiteSpace(identifier)) AddField(fields, seen, "publicId:" + (type ?? "id"), identifier);
        }
    }

    private static void AddRemarks(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        JsonElement root,
        string collectionName,
        string keyPrefix)
    {
        if (!root.TryGetProperty(collectionName, out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = GetString(item, "title");
            var description = item.TryGetProperty("description", out var desc) &&
                              desc.ValueKind == JsonValueKind.Array
                ? desc.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                : null;
            if (string.IsNullOrWhiteSpace(description)) continue;
            var key = string.IsNullOrWhiteSpace(title) ? keyPrefix : $"{keyPrefix}:{title}";
            AddField(fields, seen, key, description);
        }
    }

    private static void AddLinks(
        List<KeyValuePair<string, string>> fields,
        HashSet<string> seen,
        JsonElement root)
    {
        if (!root.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array) return;
        foreach (var item in links.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var rel = GetString(item, "rel") ?? "link";
            var href = GetString(item, "href");
            if (!string.IsNullOrWhiteSpace(href)) AddField(fields, seen, "link:" + rel, href);
        }
    }
}
