using NetCable.Core.Models;

namespace NetCable.Core.Contracts;

public interface IDnsLookupService
{
    Task<DnsQueryResult> QueryAsync(DnsQueryOptions options, CancellationToken cancellationToken = default);
}

public interface IWhoisService
{
    Task<WhoisResult> QueryAsync(WhoisOptions options, CancellationToken cancellationToken = default);
}

public interface IRdapService
{
    Task<RdapResult> QueryAsync(string query, CancellationToken cancellationToken = default);
}

public interface IIpGeolocationService
{
    Task<IpGeoInfo> LookupAsync(string ipOrHost, CancellationToken cancellationToken = default);
}

public interface ISntpService
{
    Task<SntpResult> QueryAsync(string server, int timeoutMs = 3000, CancellationToken cancellationToken = default);
}

public interface IOuiLookupService
{
    string? LookupVendor(string macAddress);
    int PrefixCount { get; }
}

public interface ISpeedTestService
{
    IAsyncEnumerable<SpeedTestProgress> RunAsync(CancellationToken cancellationToken = default);
}

public interface IHttpTlsService
{
    Task<HttpTlsInfo> InspectAsync(string url, CancellationToken cancellationToken = default);
}
