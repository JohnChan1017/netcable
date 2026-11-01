using NetCable.Core.Models;

namespace NetCable.Core.Contracts;

public interface INetworkInfoService
{
    Task<NetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken = default);
    Task<SystemInfoSummary> GetSystemInfoAsync(CancellationToken cancellationToken = default);
    Task<string?> GetPublicIpAsync(CancellationToken cancellationToken = default);
    Task<IpGeoInfo?> GetPublicIpInfoAsync(CancellationToken cancellationToken = default);
}

public interface IWifiService
{
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    Task<WifiNetworkInfo?> GetCurrentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WifiNetworkInfo>> ScanAsync(CancellationToken cancellationToken = default);
}

public interface IConnectionService
{
    Task<IReadOnlyList<ConnectionInfo>> GetConnectionsAsync(bool listeningOnly, CancellationToken cancellationToken = default);
}

public interface INeighborService
{
    Task<IReadOnlyList<NeighborEntry>> GetAsync(CancellationToken cancellationToken = default);
}
