using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetCable.Services.Network;

/// <summary>
/// 本机网络接口快照，用于判断一次“成功的 TCP 连接”是否真的是直连目标。
/// <para><b>为什么需要它：</b>本机 Meta Tunnel 这类 TUN/代理会把发往任意地址的 TCP 连接在本地代答，
/// connect 会“瞬间成功”（0ms）且源地址是 TUN 的 <c>198.18.0.1</c>。若不校验源地址，
/// 主机扫描会把不存在的 IP 全部误判为在线（实测 192.168.1.2-8 假阳性）。</para>
/// <para><b>判定规则：</b>连接源地址必须属于“非隧道接口”；且满足以下之一：</para>
/// <list type="bullet">
///   <item>目标与该源地址同子网（on-link，说明确实直连二层）；</item>
///   <item>有真实 RTT（&gt;0 ms，说明请求确实出网并往返）。</item>
/// </list>
/// <para>源地址在本机地址表中找不到（无法证明直连）、或属于识别为隧道/代理的接口，一律不采信。</para>
/// </summary>
internal sealed class LocalNetworkContext
{
    /// <summary>
    /// Live 接口名/描述关键字：TUN/TAP/VPN 类虚拟网卡。
    /// Windows 上 Meta Tunnel 的接口名是 "Meta"、描述是 "Meta Tunnel"，网络接口类型是未知值
    /// （不是 <see cref="NetworkInterfaceType.Tunnel"/>），所以必须再按名字兜底。
    /// </summary>
    private static readonly string[] ProxyInterfaceKeywords =
    {
        "tunnel", "tun", "tap", "wintun", "vpn", "wireguard", "meta", "clash",
        "utun", "ipsec", "tailscale", "zerotier", "proxy", "shadow", "sing-box"
    };

    private readonly IReadOnlyList<LocalAddress> _addresses;

    private LocalNetworkContext(IReadOnlyList<LocalAddress> addresses) => _addresses = addresses;

    /// <summary>取不到接口信息时的空快照：此时仅凭 TCP 不判定存活（保守策略）。</summary>
    public static LocalNetworkContext Empty { get; } = new(Array.Empty<LocalAddress>());

    /// <summary>枚举本机所有 IPv4 接口地址（含前缀长度），并标记隧道/代理接口与 benchmark 假 IP 段。</summary>
    public static LocalNetworkContext Create()
    {
        var addresses = new List<LocalAddress>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            bool proxyLike;
            try
            {
                proxyLike = IsProxyLikeInterface(networkInterface);
            }
            catch
            {
                proxyLike = false;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue; // 接口在枚举期间消失/权限不足时跳过
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;

                var prefixLength = unicast.PrefixLength;
                if (prefixLength is < 0 or > 32) prefixLength = 32;

                addresses.Add(new LocalAddress(
                    address,
                    prefixLength,
                    proxyLike || IsBenchmarkFakeIpRange(address)));
            }
        }

        return new LocalNetworkContext(addresses);
    }

    /// <summary>
    /// 判断一次 TCP 连接是否可采信为“直连证据”。
    /// </summary>
    /// <param name="localAddress">连接的本端地址（<c>TcpClient.Client.LocalEndPoint</c>）。</param>
    /// <param name="target">目标地址。</param>
    /// <param name="latencyMs">连接耗时（毫秒）。</param>
    public bool IsDirectConnection(IPAddress? localAddress, IPAddress target, long latencyMs)
    {
        var local = NormalizeToIPv4(localAddress);
        var remote = NormalizeToIPv4(target);
        if (local is null || remote is null) return false;

        foreach (var entry in _addresses)
        {
            if (!entry.Address.Equals(local)) continue;

            // 源地址落在 TUN/代理接口（含 Clash 的 198.18.0.0/15 fake-ip 段）→ 不采信
            if (entry.ProxyLike) return false;

            // 同子网 = 真正直连（on-link）；否则要求有真实往返时延，排除“本地代答 0ms”的情况
            return IsSameSubnet(local, remote, entry.PrefixLength) || latencyMs > 0;
        }

        // 本机地址表里没有这个源地址：无法证明直连，保守判定为不采信
        return false;
    }

    private static bool IsProxyLikeInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Tunnel)
            return true;

        return ContainsProxyKeyword(networkInterface.Name) || ContainsProxyKeyword(networkInterface.Description);
    }

    private static bool ContainsProxyKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var keyword in ProxyInterfaceKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>198.18.0.0/15 是 RFC 2544 基准测试保留段，也是 Clash/Meta 类 TUN 的默认 fake-ip 段。</summary>
    private static bool IsBenchmarkFakeIpRange(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static IPAddress? NormalizeToIPv4(IPAddress? address)
    {
        if (address is null) return null;
        if (address.AddressFamily == AddressFamily.InterNetwork) return address;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        return null;
    }

    private static bool IsSameSubnet(IPAddress left, IPAddress right, int prefixLength)
    {
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(left) & mask) == (ToUInt32(right) & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private readonly record struct LocalAddress(IPAddress Address, int PrefixLength, bool ProxyLike);
}
