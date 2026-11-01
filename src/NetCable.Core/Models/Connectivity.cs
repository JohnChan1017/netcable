namespace NetCable.Core.Models;

public sealed record PingRequest
{
    public required string Host { get; init; }
    public int Count { get; init; } = 4;
    public int BufferSize { get; init; } = 32;
    public int TimeoutMs { get; init; } = 2000;
    public int Ttl { get; init; } = 64;
    public int IntervalMs { get; init; } = 1000;
    public bool DontFragment { get; init; } = true;
}

public sealed record PingReplyInfo(int Sequence, string Address, bool Success, long? RoundTripMs, int Ttl, string? Error)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed record TracerouteOptions
{
    public required string Host { get; init; }
    public int MaxHops { get; init; } = 30;
    public int TimeoutMs { get; init; } = 2000;
    public int ProbesPerHop { get; init; } = 3;
    public bool ResolveHostNames { get; init; } = true;
}

public sealed record TracerouteHop(
    int Hop,
    string? Address,
    string? HostName,
    IReadOnlyList<long?> RoundTripMs,
    bool DestinationReached);

public sealed record PortScanOptions
{
    public required string Host { get; init; }
    public int[] Ports { get; init; } = Array.Empty<int>();
    public int StartPort { get; init; } = 1;
    public int EndPort { get; init; } = 1024;
    public int TimeoutMs { get; init; } = 1200;
    public int Concurrency { get; init; } = 128;
    public bool ResolveService { get; init; } = true;
}

public sealed record PortScanResult(int Port, bool Open, string? Service, string? Banner, long? LatencyMs);

public sealed record HostScanOptions
{
    public required string Range { get; init; }
    public int TimeoutMs { get; init; } = 1200;
    public int Concurrency { get; init; } = 64;
    public bool ResolveHostNames { get; init; } = true;
    public bool ResolveMacAddresses { get; init; } = true;
}

public sealed record DiscoveredHost(
    string IpAddress,
    bool IsAlive,
    long? LatencyMs,
    string? HostName,
    string? MacAddress,
    string? Vendor);

public sealed record WakeOnLanOptions
{
    public required string MacAddress { get; init; }
    public string? BroadcastAddress { get; init; }
    public int Port { get; init; } = 9;
}

public sealed record SubnetInfo(
    string Input,
    string NetworkAddress,
    string BroadcastAddress,
    string Netmask,
    int PrefixLength,
    string? FirstUsable,
    string? LastUsable,
    long TotalAddresses,
    long UsableHosts,
    bool IsPrivate,
    string NetworkCidr);
