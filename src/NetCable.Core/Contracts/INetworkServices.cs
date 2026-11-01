using NetCable.Core.Models;

namespace NetCable.Core.Contracts;

public interface IPingService
{
    IAsyncEnumerable<PingReplyInfo> PingAsync(PingRequest request, CancellationToken cancellationToken = default);
}

public interface ITracerouteService
{
    IAsyncEnumerable<TracerouteHop> TraceAsync(TracerouteOptions options, CancellationToken cancellationToken = default);
}

public interface IPortScannerService
{
    IAsyncEnumerable<PortScanResult> ScanAsync(PortScanOptions options, CancellationToken cancellationToken = default);
}

public interface IHostScannerService
{
    IAsyncEnumerable<DiscoveredHost> ScanAsync(HostScanOptions options, CancellationToken cancellationToken = default);
}

public interface IWakeOnLanService
{
    Task SendAsync(WakeOnLanOptions options, CancellationToken cancellationToken = default);
}

public interface ISubnetCalculator
{
    SubnetInfo Calculate(string cidrOrIp);
    IReadOnlyList<string> ExpandRange(string range, int maxHosts = 4096);
}
