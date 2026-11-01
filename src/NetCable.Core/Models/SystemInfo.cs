namespace NetCable.Core.Models;

public sealed record NetworkInterfaceInfo(
    string Name,
    string? Description,
    string? MacAddress,
    IReadOnlyList<string> IPv4Addresses,
    IReadOnlyList<string> IPv6Addresses,
    string? SubnetMask,
    bool IsUp,
    long? SpeedBitsPerSecond,
    string? InterfaceType);

public sealed record NetworkInfo(
    string HostName,
    string? DefaultGateway,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<NetworkInterfaceInfo> Interfaces,
    string? CurrentIp,
    string? SubnetCidr);

public sealed record SystemInfoSummary(
    string HostName,
    string OsDisplayName,
    string Architecture,
    string DotNetVersion,
    bool IsElevated,
    string? UserName,
    bool WifiSupported);

public sealed record WifiNetworkInfo(
    string Ssid,
    string? Bssid,
    int? SignalPercent,
    int? Channel,
    string? Band,
    string? Security,
    string? Rate,
    bool IsConnected);

public sealed record ConnectionInfo(
    string Protocol,
    string LocalEndPoint,
    string RemoteEndPoint,
    string State,
    int? Pid,
    string? ProcessName);

public sealed record NeighborEntry(
    string IpAddress,
    string MacAddress,
    string State,
    string Interface);
