using NetCable.Core.Models;
using NetCable.Core.Platform;

namespace NetCable.Services.Platform.Internal;

/// <summary>一次网络信息采集的原始结果，由各平台读取器填充。</summary>
internal sealed record NetworkFacts(
    string? DefaultGateway,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<NetworkInterfaceInfo> Interfaces,
    string? CurrentIp,
    string? SubnetCidr)
{
    /// <summary>空结果，用于平台未知或采集彻底失败时。</summary>
    public static NetworkFacts Empty { get; } =
        new(null, Array.Empty<string>(), Array.Empty<NetworkInterfaceInfo>(), null, null);

    /// <summary>按当前操作系统分派到对应的读取器。</summary>
    public static Task<NetworkFacts> ReadAsync(CancellationToken cancellationToken = default) =>
        PlatformInfo.Family switch
        {
            OsFamily.Windows => WindowsNetworkFactsReader.ReadAsync(cancellationToken),
            OsFamily.MacOS => MacNetworkFactsReader.ReadAsync(cancellationToken),
            OsFamily.Linux => LinuxNetworkFactsReader.ReadAsync(cancellationToken),
            _ => Task.FromResult(Empty)
        };
}

/// <summary>把各平台读取到的网卡、网关与 DNS 组装成最终结果（计算主 IP 与 CIDR）。</summary>
internal static class NetworkFactsComposer
{
    /// <summary>组装网络信息；优先使用默认路由所在网卡作为“当前主 IP”。</summary>
    public static NetworkFacts Compose(
        IReadOnlyList<NetworkInterfaceInfo> interfaces,
        string? gateway,
        IReadOnlyList<string> dnsServers,
        string? preferredInterfaceName)
    {
        NetworkInterfaceInfo? preferred = null;
        if (!string.IsNullOrWhiteSpace(preferredInterfaceName))
        {
            preferred = interfaces.FirstOrDefault(i =>
                string.Equals(i.Name, preferredInterfaceName, StringComparison.OrdinalIgnoreCase));
        }

        preferred ??= interfaces.FirstOrDefault(i =>
                             i.IsUp && i.IPv4Addresses.Count > 0 &&
                             !string.Equals(i.InterfaceType, "Tunnel", StringComparison.OrdinalIgnoreCase))
                     ?? interfaces.FirstOrDefault(i => i.IPv4Addresses.Count > 0);

        var currentIp = preferred?.IPv4Addresses.FirstOrDefault();
        var subnetCidr = currentIp is null ? null : PlatformText.MakeCidr(currentIp, preferred?.SubnetMask);

        var dns = dnsServers
            .Where(d => !string.IsNullOrWhiteSpace(d) && !PlatformText.IsLoopbackOrUnspecified(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(gateway))
        {
            gateway = gateway.Trim();
            var scopeIndex = gateway.IndexOf('%');
            if (scopeIndex > 0) gateway = gateway[..scopeIndex];
        }

        return new NetworkFacts(gateway, dns, interfaces, currentIp, subnetCidr);
    }
}
