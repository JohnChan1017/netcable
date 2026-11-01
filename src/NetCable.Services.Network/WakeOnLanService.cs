using System.Net;
using System.Net.Sockets;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Utils;

namespace NetCable.Services.Network;

/// <summary>
/// Wake-on-LAN：构造魔术包（6 字节 0xFF + 目标 MAC 重复 16 次，共 102 字节），
/// 通过 UDP 广播到默认 <c>255.255.255.255:9</c>，也可指定子网定向广播地址与端口。
/// <para>三端实现一致（UDP 广播不区分平台）；<see cref="UdpClient.EnableBroadcast"/> 在
/// Windows/Linux/macOS 上都必需，否则发送到 255.255.255.255 会被内核拒绝（EACCES/EPERM）。</para>
/// <para>输入非法时抛 <see cref="ArgumentException"/>（由 UI 统一捕获展示）；
/// 发送失败包装为 <see cref="InvalidOperationException"/>，不会让进程崩溃。</para>
/// </summary>
public sealed class WakeOnLanService : IWakeOnLanService
{
    /// <summary>魔术包中 MAC 的重复次数（WoL 规范固定 16 次）。</summary>
    private const int MagicPacketRepeatCount = 16;

    /// <summary>MAC 长度（字节）。</summary>
    private const int MacByteCount = 6;

    /// <summary>发送魔术包；<paramref name="cancellationToken"/> 可中断 UDP 发送。</summary>
    public async Task SendAsync(WakeOnLanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var packet = BuildMagicPacket(options.MacAddress);
        var endpoint = ResolveEndpoint(options);

        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            await udp.SendAsync(packet, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"发送 Wake-on-LAN 魔术包到 {endpoint} 失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 按 WoL 规范构造魔术包：<c>FF×6 + MAC×16</c>。MAC 输入格式非法时抛 <see cref="ArgumentException"/>。
    /// 公开为静态方法便于 UI/测试在不发包的情况下校验 MAC 与包结构。
    /// </summary>
    public static byte[] BuildMagicPacket(string macAddress)
    {
        var mac = ParseMac(macAddress);
        var packet = new byte[MacByteCount + MagicPacketRepeatCount * MacByteCount];

        for (var i = 0; i < MacByteCount; i++) packet[i] = 0xFF;
        for (var repeat = 0; repeat < MagicPacketRepeatCount; repeat++)
            Buffer.BlockCopy(mac, 0, packet, MacByteCount + repeat * MacByteCount, MacByteCount);

        return packet;
    }

    /// <summary>
    /// 解析 MAC：支持 <c>AA:BB:CC:DD:EE:FF</c>、<c>AA-BB-...</c>、<c>AA BB ...</c> 与连续 12 位十六进制。
    /// </summary>
    private static byte[] ParseMac(string? macAddress)
    {
        // NormalizeMac 会忽略分隔符抽取十六进制，这里要求恰好 12 位（6 字节）
        var hex = OuiLookup.NormalizeMac(macAddress);
        if (hex is null || hex.Length != 12)
        {
            throw new ArgumentException(
                $"MAC 地址格式无效：{macAddress}（应为 AA:BB:CC:DD:EE:FF 或连续 12 位十六进制）",
                nameof(macAddress));
        }

        var mac = new byte[MacByteCount];
        for (var i = 0; i < MacByteCount; i++)
            mac[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return mac;
    }

    /// <summary>解析目标广播地址与端口；默认 255.255.255.255:9。</summary>
    private static IPEndPoint ResolveEndpoint(WakeOnLanOptions options)
    {
        var port = options.Port;
        if (port is < 1 or > 65535)
            throw new ArgumentException($"端口无效：{port}（应在 1-65535 之间）", nameof(options));

        var addressText = string.IsNullOrWhiteSpace(options.BroadcastAddress)
            ? "255.255.255.255"
            : options.BroadcastAddress.Trim();

        // WoL 依赖 IPv4 二层广播；IPv6 没有等价的广播语义（可用组播，但不在本工具范围）
        if (!IPAddress.TryParse(addressText, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                $"广播地址无效：{addressText}（Wake-on-LAN 仅支持 IPv4 广播地址，如 192.168.1.255）",
                nameof(options));
        }

        return new IPEndPoint(address, port);
    }
}
