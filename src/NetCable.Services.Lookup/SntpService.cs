using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Lookup;

/// <summary>
/// SNTP 时间同步服务。向 UDP 123 发送标准 48 字节请求，解析服务器响应中的
/// 接收/发送时间戳，计算本地时钟偏差与网络往返时延。超时或异常写入 Error，不抛异常。
/// </summary>
public sealed class SntpService : ISntpService
{
    private const int NtpPort = 123;
    private const int NtpPacketSize = 48;
    private const double NtpEpochOffsetSeconds = 2208988800d; // 1900-01-01 → 1970-01-01
    private const double FractionTicksScale = 10_000_000d / 4294967296d;

    /// <summary>向指定 SNTP 服务器查询时间。默认超时 3000 ms。</summary>
    public async Task<SntpResult> QueryAsync(
        string server,
        int timeoutMs = 3000,
        CancellationToken cancellationToken = default)
    {
        var host = server?.Trim() ?? string.Empty;
        if (timeoutMs <= 0) timeoutMs = 3000;
        if (host.Length == 0) return Failure(host, "SNTP 服务器为空");

        try
        {
            var address = await ResolveAddressAsync(host, cancellationToken).ConfigureAwait(false);
            if (address is null) return Failure(host, $"无法解析 SNTP 服务器：{host}");

            // 48 字节请求：LI=0、VN=4、Mode=3（客户端）
            var packet = new byte[NtpPacketSize];
            packet[0] = 0x23;

            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using var cts = LookupHttp.CreateTimeoutSource(cancellationToken, timeoutMs);
            var endPoint = new IPEndPoint(address, NtpPort);

            var sentAt = DateTimeOffset.UtcNow;
            await socket.ConnectAsync(endPoint, cts.Token).ConfigureAwait(false);
            await socket.SendAsync(packet, SocketFlags.None, cts.Token).ConfigureAwait(false);

            var buffer = new byte[68];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
            var receivedAt = DateTimeOffset.UtcNow;

            if (received < NtpPacketSize)
                return Failure(host, $"SNTP 响应长度不足（{received} 字节）");

            var serverReceived = ReadTimestamp(buffer, 32);
            var serverTransmitted = ReadTimestamp(buffer, 40);
            if (serverTransmitted is null)
                return Failure(host, "SNTP 响应中的发送时间戳无效");

            var localRoundTrip = receivedAt - sentAt;
            var serverProcessing = serverReceived is not null
                ? serverTransmitted.Value - serverReceived.Value
                : TimeSpan.Zero;
            var roundTrip = localRoundTrip - serverProcessing;
            if (roundTrip < TimeSpan.Zero) roundTrip = TimeSpan.Zero;

            var localMidpoint = sentAt + TimeSpan.FromTicks((receivedAt - sentAt).Ticks / 2);
            var offset = serverTransmitted.Value - localMidpoint;

            return new SntpResult(host, serverTransmitted.Value, offset, roundTrip, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(host, $"SNTP 请求超时（{timeoutMs} ms）");
        }
        catch (Exception ex)
        {
            return Failure(host, LookupErrors.Describe(ex, timeoutMs));
        }
    }

    private static async Task<IPAddress?> ResolveAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            // UDP/123 的 IPv6 链路在部分网络不可用，优先 IPv4
            return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadTimestamp(byte[] buffer, int offset)
    {
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 4, 4));
        if (seconds == 0 && fraction == 0) return null;

        double unixSeconds = seconds - NtpEpochOffsetSeconds;
        if (seconds < NtpEpochOffsetSeconds) unixSeconds += 4294967296d; // NTP 纪元 0（1900-2036）
        var ticks = (long)Math.Round(fraction * FractionTicksScale);
        return DateTimeOffset.UnixEpoch.AddSeconds(unixSeconds).AddTicks(ticks);
    }

    private static SntpResult Failure(string server, string error) =>
        new(server, default, TimeSpan.Zero, TimeSpan.Zero, error);
}
