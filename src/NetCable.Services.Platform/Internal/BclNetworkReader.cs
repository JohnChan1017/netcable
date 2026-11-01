using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// 基于 .NET 跨平台网络 API 的网卡信息读取器。
/// 作为各平台系统命令解析失败（命令缺失、无权限、输出格式变化）时的兜底来源，
/// 同时在命令解析成功时用于补齐速率、状态、接口类型等命令里没有的字段。
/// </summary>
internal static class BclNetworkReader
{
    /// <summary>读取网卡、默认网关与 DNS，并组装成 <see cref="NetworkFacts"/>。</summary>
    public static NetworkFacts ReadFacts()
    {
        var interfaces = new List<NetworkInterfaceInfo>();
        var dnsServers = new List<string>();
        string? gateway = null;
        string? gatewayInterfaceName = null;

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (PlatformText.IsLoopbackInterfaceName(networkInterface.Name, networkInterface.Description)) continue;

                IPInterfaceProperties properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var ipv4 = new List<string>();
                var ipv6 = new List<string>();
                string? subnetMask = null;

                try
                {
                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var address = unicast.Address.ToString();
                            if (!ipv4.Contains(address)) ipv4.Add(address);

                            if (subnetMask is null)
                            {
                                try
                                {
                                    var mask = unicast.IPv4Mask?.ToString();
                                    if (!string.IsNullOrWhiteSpace(mask) && mask != "0.0.0.0") subnetMask = mask;
                                }
                                catch
                                {
                                    // 某些虚拟网卡不提供 IPv4Mask，忽略即可
                                }
                            }
                        }
                        else if (unicast.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (IPAddress.IsLoopback(unicast.Address)) continue;
                            var address = unicast.Address.ToString();
                            var scopeIndex = address.IndexOf('%');
                            if (scopeIndex > 0) address = address[..scopeIndex];
                            if (!ipv6.Contains(address)) ipv6.Add(address);
                        }
                    }
                }
                catch
                {
                    // 部分隧道网卡在枚举地址时会抛异常
                }

                var mac = PlatformText.NormalizeMac(ReadMacAddress(networkInterface));
                long? speed = networkInterface.Speed > 0 ? networkInterface.Speed : null;

                interfaces.Add(new NetworkInterfaceInfo(
                    networkInterface.Name,
                    string.IsNullOrWhiteSpace(networkInterface.Description) ? null : networkInterface.Description,
                    mac,
                    ipv4,
                    ipv6,
                    subnetMask,
                    networkInterface.OperationalStatus == OperationalStatus.Up,
                    speed,
                    DescribeInterfaceType(networkInterface.NetworkInterfaceType)));

                if (gateway is null)
                {
                    try
                    {
                        foreach (var gatewayAddress in properties.GatewayAddresses)
                        {
                            var address = gatewayAddress.Address;
                            if (address is null) continue;
                            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) continue;
                            if (IPAddress.IsLoopback(address)) continue;
                            // 优先取 IPv4 默认网关，IPv6 网关留作兜底
                            if (address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                gateway = address.ToString();
                                gatewayInterfaceName = networkInterface.Name;
                                break;
                            }

                            gateway ??= address.ToString();
                            gatewayInterfaceName ??= networkInterface.Name;
                        }
                    }
                    catch
                    {
                        // 忽略单块网卡的网关读取失败
                    }
                }

                try
                {
                    foreach (var dns in properties.DnsAddresses)
                    {
                        var address = dns.ToString();
                        if (PlatformText.IsLoopbackOrUnspecified(address)) continue;
                        if (!dnsServers.Contains(address, StringComparer.OrdinalIgnoreCase)) dnsServers.Add(address);
                    }
                }
                catch
                {
                    // 忽略 DNS 读取失败
                }
            }
        }
        catch
        {
            // 枚举网卡整体失败（极少数受限环境），返回空集合
        }

        var preferred = gatewayInterfaceName;
        return NetworkFactsComposer.Compose(interfaces, gateway, dnsServers, preferred);
    }

    private static string? ReadMacAddress(NetworkInterface networkInterface)
    {
        try
        {
            var physical = networkInterface.GetPhysicalAddress();
            if (physical is null || physical.GetAddressBytes().Length < 6) return null;
            return physical.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 .NET 的接口类型枚举映射为 UI 友好的字符串；
    /// 未知类型（Windows 会直接透出原始 IfType 数值，例如 Meta 网卡的 53）返回 null，
    /// 由各平台读取器按名称/描述推断。
    /// </summary>
    private static string? DescribeInterfaceType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.GigabitEthernet => "Ethernet",
        NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Wman
            or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Wireless80211",
        NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Slip => "Tunnel",
        NetworkInterfaceType.Loopback => "Loopback",
        _ => null
    };
}
