using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Network;

/// <summary>
/// 802.11 monitor mode 管理帧捕获（Windows + Npcap rfmon）。
/// <para><see cref="GetCapabilityAsync"/> 走完整的 rfmon 探测流程：
/// <c>pcap_create</c> → <c>pcap_can_set_rfmon</c> → <c>pcap_set_rfmon</c> →
/// <c>pcap_set_snaplen/promisc/timeout</c> → <c>pcap_activate</c> → <c>pcap_datalink</c>。
/// 网卡/驱动不支持 monitor mode 时返回 <c>Supported=false</c> 与可操作的中文原因
/// （本机 Intel 无线网卡 即属于此情况）。</para>
/// <para><see cref="CaptureAsync"/> 以流式方式产出管理帧（Beacon / Probe / Auth / Assoc / Deauth 等），
/// 解析在独立线程进行（pcap_next_ex 为阻塞调用，超时 200ms），单帧解析异常直接跳过。</para>
/// </summary>
public sealed class WifiMonitorService : IWifiMonitorService
{
    private const int MaxDurationSeconds = 120;
    private const int MaxFramesInFlight = 2048;

    private static readonly string[] WirelessKeywords =
    {
        "wi-fi", "wifi", "wlan", "wireless", "802.11", "无线"
    };

    private static readonly string[] VirtualKeywords =
    {
        "wi-fi direct", "virtual", "wfp", "qos packet scheduler", "native wifi filter",
        "bluetooth", "蓝牙", "meta", "tunnel", "loopback", "回环", "本地连接"
    };

    /// <inheritdoc />
    public async Task<WifiMonitorCapability> GetCapabilityAsync(CancellationToken cancellationToken = default)
    {
        // pcap_create/activate 是阻塞调用，放到线程池执行，避免阻塞 UI 线程
        return await Task.Run(ProbeCapability, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<WifiFrameInfo> CaptureAsync(
        int durationSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seconds = Math.Clamp(durationSeconds, 1, MaxDurationSeconds);

        MonitorOpenResult? opened = null;
        try
        {
            opened = OpenMonitor(keepSession: true);
        }
        catch
        {
            opened = null; // 打不开会话：返回空流，不抛异常
        }

        if (opened is null || !opened.Capability.Supported || opened.Session is null || opened.Backend is null)
            yield break;

        var backend = opened.Backend;
        var session = opened.Session;

        var channel = Channel.CreateBounded<WifiFrameInfo>(new BoundedChannelOptions(MaxFramesInFlight)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => ProduceFrames(
            backend, session, channel.Writer, TimeSpan.FromSeconds(seconds), producerCts.Token));

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var frame))
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            // 通知抓包线程退出；会话由抓包线程在 finally 里 Dispose（不能在 pcap_next_ex 进行中关 handle）
            producerCts.Cancel();
            channel.Writer.TryComplete();
        }
    }

    private static WifiMonitorCapability ProbeCapability()
    {
        try
        {
            return OpenMonitor(keepSession: false).Capability;
        }
        catch (Exception ex)
        {
            return new WifiMonitorCapability(false, $"检测 monitor mode 失败：{ex.Message}", null, null);
        }
    }

    /// <summary>
    /// 探测并（可选）保持一个 rfmon 会话。失败时 <see cref="MonitorOpenResult.Capability"/> 为 false
    /// 且 Reason 为中文可操作原因。
    /// </summary>
    private static MonitorOpenResult OpenMonitor(bool keepSession)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("802.11 monitor mode 捕获当前仅支持 Windows（依赖 Npcap 的 rfmon 能力）；" +
                        "macOS/Linux 版本尚未实现。");
        }

        var backend = NpcapRfmonBackend.TryGet(out var loadError);
        if (backend is null)
        {
            return Fail($"monitor mode 不可用：{loadError} " +
                        "请安装 Npcap（https://npcap.com），安装时勾选 \"Support raw 802.11 traffic (and monitor mode)\"。");
        }

        List<NpcapDevice> devices;
        try
        {
            devices = backend.GetDevices(out var enumError);
            if (devices.Count == 0)
            {
                var detail = string.IsNullOrWhiteSpace(enumError) ? string.Empty : $"（{enumError}）";
                return Fail($"monitor mode 不可用：Npcap 未枚举到任何抓包设备{detail}。");
            }
        }
        catch (Exception ex)
        {
            return Fail($"monitor mode 不可用：枚举 Npcap 设备失败：{ex.Message}");
        }

        var device = SelectWirelessDevice(devices);
        if (device is null)
        {
            return Fail("monitor mode 不可用：Npcap 设备列表中未找到无线网卡（Wi-Fi / WLAN 适配器）。" +
                        "请确认无线网卡已启用，并安装 Npcap 时勾选 \"Support raw 802.11 traffic (and monitor mode)\"。");
        }

        if (!backend.TryOpen(device, out var session, out var reason))
        {
            return Fail(reason ?? "monitor mode 打开失败。", device);
        }

        var capability = new WifiMonitorCapability(true, null, device.DisplayName, device.Description);

        if (!keepSession)
        {
            session!.Dispose();
            return new MonitorOpenResult(backend, capability, null);
        }

        return new MonitorOpenResult(backend, capability, session);
    }

    /// <summary>抓包线程：循环 pcap_next_ex → 解析 → 只把管理帧写入 channel；退出时关闭会话。</summary>
    private static void ProduceFrames(
        NpcapRfmonBackend backend,
        RfmonSession session,
        ChannelWriter<WifiFrameInfo> writer,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < duration && !cancellationToken.IsCancellationRequested)
            {
                var read = backend.ReadNextPacket(session);
                if (read.Status == RfmonReadResult.StatusError) break;
                if (read.Status != RfmonReadResult.StatusPacket || read.Frame is null) continue;

                try
                {
                    var frame = WifiFrameParser.Parse(read.Frame, session.DataLink);
                    if (frame is null) continue;

                    // 本工具只关注 802.11 管理帧；数据/控制帧直接丢弃，避免刷屏
                    if (!string.Equals(frame.FrameType, WifiFrameParser.ManagementFrameType, StringComparison.Ordinal))
                        continue;

                    writer.TryWrite(frame);
                }
                catch
                {
                    // 单帧解析异常跳过
                }
            }
        }
        finally
        {
            writer.TryComplete();
            session.Dispose(); // 退出 monitor mode
        }
    }

    /// <summary>从 Npcap 设备列表里挑选无线网卡（排除 Wi-Fi Direct / WFP / 虚拟网卡）。</summary>
    private static NpcapDevice? SelectWirelessDevice(List<NpcapDevice> devices)
    {
        foreach (var device in devices)
        {
            var text = (device.Description ?? string.Empty) + " " + device.Name;
            if (ContainsAny(text, VirtualKeywords)) continue;
            if (ContainsAny(text, WirelessKeywords)) return device;
        }

        return null;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static MonitorOpenResult Fail(string reason, NpcapDevice? device = null) =>
        new(null,
            new WifiMonitorCapability(false, reason, device?.DisplayName, device?.Description),
            null);

    private sealed record MonitorOpenResult(
        NpcapRfmonBackend? Backend,
        WifiMonitorCapability Capability,
        RfmonSession? Session);
}
