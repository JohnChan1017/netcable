namespace NetCable.Core.Models;

public enum DnsRecordType
{
    A = 1,
    Ns = 2,
    Cname = 5,
    Soa = 6,
    Ptr = 12,
    Mx = 15,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Caa = 257,
    Any = 255
}

public sealed record DnsQueryOptions
{
    public required string Name { get; init; }
    public DnsRecordType Type { get; init; } = DnsRecordType.A;
    public string? Server { get; init; }
    public int TimeoutMs { get; init; } = 3000;
}

public sealed record DnsRecordInfo(string Name, string Type, string Value, int Ttl);

public sealed record DnsQueryResult(
    string Name,
    string Server,
    TimeSpan Elapsed,
    IReadOnlyList<DnsRecordInfo> Records,
    string? Error);

public sealed record WhoisOptions
{
    public required string Query { get; init; }
    public string? Server { get; init; }
    public int TimeoutMs { get; init; } = 10000;
}

public sealed record WhoisResult(
    string Query,
    string Server,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    string Raw,
    string? Error);

public sealed record RdapResult(
    string Query,
    string? Handle,
    string? Name,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    string RawJson,
    string? Error);

public sealed record IpGeoInfo(
    string Ip,
    string? Country,
    string? CountryCode,
    string? Region,
    string? City,
    string? Isp,
    string? Organization,
    string? Asn,
    double? Latitude,
    double? Longitude,
    string? TimeZone);

public sealed record SntpResult(
    string Server,
    DateTimeOffset ServerTime,
    TimeSpan Offset,
    TimeSpan RoundTrip,
    string? Error);

public sealed record SpeedTestProgress(string Phase, double Fraction, double? DownloadMbps, double? UploadMbps, double? LatencyMs);

public sealed record SpeedTestResult(double LatencyMs, double DownloadMbps, double UploadMbps, string Server);

public sealed record HttpTlsHeader(string Name, string Value);

public sealed record TlsCertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string? Thumbprint,
    IReadOnlyList<string> DnsNames,
    string? Protocol);

public sealed record HttpTlsInfo(
    string Url,
    string? FinalUrl,
    int? StatusCode,
    string? StatusText,
    string? HttpVersion,
    string? Server,
    long ElapsedMs,
    IReadOnlyList<HttpTlsHeader> Headers,
    TlsCertificateInfo? Certificate,
    IReadOnlyList<string> Redirects,
    string? Error);
