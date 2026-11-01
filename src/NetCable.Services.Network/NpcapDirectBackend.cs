using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NetCable.Core.Models;

namespace NetCable.Services.Network;

/// <summary>
/// Npcap 直连后端：不依赖 Wireshark/dumpcap，直接通过 <c>NativeLibrary.Load</c> 加载
/// <c>%WINDIR%\System32\Npcap\wpcap.dll</c>，用 pcap_findalldevs / pcap_open_live /
/// pcap_next_ex / pcap_close / pcap_geterr 完成网卡枚举与实时抓包。
/// 抓到的链路层帧交给 <see cref="DiscoveryProtocolService.ParsePacket"/> 复用既有 LLDP/CDP 解析。
/// 该实现仅用于 Windows；任何加载/枚举/抓包失败都转换为中文错误文本，不抛出异常。
/// </summary>
internal sealed class NpcapDirectBackend
{
    public const string BackendName = "Npcap 直连";

    private const int PcapErrBufSize = 256;
    private const int Snaplen = 2000;
    private const int Promiscuous = 1;
    private const int ReadTimeoutMs = 200;
    private const int MaxPacketLength = 262_144;

    /// <summary>
    /// Windows 的 <c>struct pcap_pkthdr</c> 布局为 <c>struct timeval ts</c>（2 × uint32 = 8 字节）
    /// + caplen(uint32) + len(uint32)，故 caplen 固定位于第 8 字节（32/64 位相同）。
    /// </summary>
    private const int PcapHeaderCaptureLengthOffset = 8;

    private static readonly Lazy<NpcapProbeResult> Shared =
        new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly nint _library;
    private readonly PcapFindAllDevs _findAllDevs;
    private readonly PcapFreeAllDevs? _freeAllDevs;
    private readonly PcapOpenLive _openLive;
    private readonly PcapNextEx _nextEx;
    private readonly PcapClose _close;
    private readonly PcapGetErr _getErr;
    private readonly PcapDataLink _dataLink;

    private NpcapDirectBackend(
        string libraryPath,
        nint library,
        PcapFindAllDevs findAllDevs,
        PcapFreeAllDevs? freeAllDevs,
        PcapOpenLive openLive,
        PcapNextEx nextEx,
        PcapClose close,
        PcapGetErr getErr,
        PcapDataLink dataLink)
    {
        LibraryPath = libraryPath;
        _library = library;
        _findAllDevs = findAllDevs;
        _freeAllDevs = freeAllDevs;
        _openLive = openLive;
        _nextEx = nextEx;
        _close = close;
        _getErr = getErr;
        _dataLink = dataLink;
    }

    /// <summary>已加载的 wpcap.dll 完整路径。</summary>
    public string LibraryPath { get; }

    /// <summary>获取进程级共享的 Npcap 后端实例（加载失败时返回 null 并给出原因）。</summary>
    public static NpcapDirectBackend? TryGet(out string? error)
    {
        var result = Shared.Value;
        error = result.Error;
        return result.Backend;
    }

    /// <summary>枚举 Npcap 抓包设备；失败返回空列表并给出原因。</summary>
    public List<NpcapDevice> GetDevices(out string? error)
    {
        var devices = new List<NpcapDevice>();
        error = null;

        var errbuf = Marshal.AllocHGlobal(PcapErrBufSize);
        try
        {
            ZeroBuffer(errbuf);
            var rc = _findAllDevs(out var deviceList, errbuf);
            if (rc != 0)
            {
                error = $"{BackendName} 枚举网卡失败：{ReadAnsi(errbuf)}";
                return devices;
            }

            try
            {
                var current = deviceList;
                var guard = 0;
                while (current != nint.Zero && guard++ < 512)
                {
                    var namePointer = Marshal.ReadIntPtr(current, nint.Size);
                    var descriptionPointer = Marshal.ReadIntPtr(current, nint.Size * 2);
                    var name = namePointer == nint.Zero ? null : Marshal.PtrToStringAnsi(namePointer);
                    var description = descriptionPointer == nint.Zero ? null : Marshal.PtrToStringAnsi(descriptionPointer);
                    if (!string.IsNullOrWhiteSpace(name)) devices.Add(new NpcapDevice(name, description));
                    current = Marshal.ReadIntPtr(current);
                }
            }
            finally
            {
                if (_freeAllDevs is not null && deviceList != nint.Zero) _freeAllDevs(deviceList);
            }
        }
        catch (Exception ex)
        {
            error = $"{BackendName} 枚举网卡异常：{ex.Message}";
        }
        finally
        {
            Marshal.FreeHGlobal(errbuf);
        }

        return devices;
    }

    /// <summary>
    /// 选择抓包设备：优先与系统活动网卡（有默认网关、非回环/虚拟过滤网卡）按 GUID 匹配，
    /// 其次按 Wi-Fi/以太网关键字挑选，最后退到第一个非回环/非虚拟设备。
    /// </summary>
    public NpcapDevice? SelectDevice(out string? error)
    {
        var devices = GetDevices(out error);
        if (devices.Count == 0)
        {
            error ??= $"{BackendName} 未枚举到任何抓包设备。";
            return null;
        }

        try
        {
            foreach (var nic in EnumerateActiveInterfaces())
            {
                var match = MatchDevice(devices, nic);
                if (match is not null) return match;
            }
        }
        catch
        {
            // 网卡枚举失败时继续走关键字兜底
        }

        var preferred = devices
            .Where(device => !IsRejectedDevice(device))
            .FirstOrDefault(device => HasPreferredKeyword(device));
        if (preferred is not null) return preferred;

        var fallback = devices.FirstOrDefault(device => !IsRejectedDevice(device));
        if (fallback is not null) return fallback;

        error = $"{BackendName} 只枚举到回环/虚拟网卡，未找到可用抓包设备。";
        return null;
    }

    /// <summary>
    /// 实时抓包至指定时长。返回已解析邻居与帧计数；失败时 Error 给出中文原因，
    /// 已抓到的帧仍会保留（部分成功）。取消时抛出 OperationCanceledException 由调用方处理。
    /// </summary>
    public NpcapCaptureResult Capture(
        NpcapDevice device,
        int durationSeconds,
        string interfaceLabel,
        CancellationToken cancellationToken)
    {
        var result = new NpcapCaptureResult();
        var errbuf = Marshal.AllocHGlobal(PcapErrBufSize);
        nint handle = nint.Zero;
        nint headerSlot = nint.Zero;
        nint dataSlot = nint.Zero;

        try
        {
            ZeroBuffer(errbuf);
            handle = _openLive(device.Name, Snaplen, Promiscuous, ReadTimeoutMs, errbuf);
            if (handle == nint.Zero)
            {
                result.Error = $"{BackendName} 打开网卡失败：{ReadAnsi(errbuf)}";
                return result;
            }

            result.DataLink = _dataLink(handle);
            headerSlot = Marshal.AllocHGlobal(nint.Size);
            dataSlot = Marshal.AllocHGlobal(nint.Size);

            var stopwatch = Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1, 60));

            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Marshal.WriteIntPtr(headerSlot, nint.Zero);
                Marshal.WriteIntPtr(dataSlot, nint.Zero);
                var rc = _nextEx(handle, headerSlot, dataSlot);

                if (rc == 0) continue; // 读超时（200ms），继续下一轮
                if (rc == -1)
                {
                    result.Error = $"{BackendName} 抓包错误：{ReadPcapError(handle)}";
                    break;
                }
                if (rc != 1) break; // -2 = EOF（实时抓包不会出现）

                var header = Marshal.ReadIntPtr(headerSlot);
                var data = Marshal.ReadIntPtr(dataSlot);
                if (header == nint.Zero || data == nint.Zero) continue;

                var captureLength = Marshal.ReadInt32(header, PcapHeaderCaptureLengthOffset);
                if (captureLength <= 0 || captureLength > MaxPacketLength) continue;

                var frame = new byte[captureLength];
                Marshal.Copy(data, frame, 0, captureLength);
                result.Frames++;

                try
                {
                    var neighbor = DiscoveryProtocolService.ParsePacket(
                        frame, (uint)result.DataLink, interfaceLabel);
                    if (neighbor is null) continue;
                    if (neighbor.Protocol == "LLDP") result.LldpFrames++;
                    else if (neighbor.Protocol == "CDP") result.CdpFrames++;
                    result.Neighbors.Add(neighbor);
                }
                catch
                {
                    // 单帧解析异常跳过
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error ??= $"{BackendName} 抓包异常：{ex.Message}";
        }
        finally
        {
            if (headerSlot != nint.Zero) Marshal.FreeHGlobal(headerSlot);
            if (dataSlot != nint.Zero) Marshal.FreeHGlobal(dataSlot);
            if (handle != nint.Zero) _close(handle);
            Marshal.FreeHGlobal(errbuf);
        }

        return result;
    }

    // ------------------------------------------------------------------ 加载与探测

    private static NpcapProbeResult Probe()
    {
        if (!OperatingSystem.IsWindows())
            return new NpcapProbeResult(null, $"{BackendName} 仅支持 Windows。");

        var libraryPath = ResolveLibraryPath();
        if (libraryPath is null)
            return new NpcapProbeResult(null, "未找到 wpcap.dll（Npcap 未安装）。");

        nint library = nint.Zero;
        try
        {
            // 先加载同目录依赖（Packet.dll），避免 wpcap.dll 因依赖缺失而加载失败
            PreloadDependencies(libraryPath);

            library = NativeLibrary.Load(libraryPath);
            var findAllDevs = GetDelegate<PcapFindAllDevs>(library, "pcap_findalldevs");
            var openLive = GetDelegate<PcapOpenLive>(library, "pcap_open_live");
            var nextEx = GetDelegate<PcapNextEx>(library, "pcap_next_ex");
            var close = GetDelegate<PcapClose>(library, "pcap_close");
            var getErr = GetDelegate<PcapGetErr>(library, "pcap_geterr");
            var dataLink = GetDelegate<PcapDataLink>(library, "pcap_datalink");
            var freeAllDevs = TryGetDelegate<PcapFreeAllDevs>(library, "pcap_freealldevs");

            var backend = new NpcapDirectBackend(
                libraryPath, library, findAllDevs, freeAllDevs, openLive, nextEx, close, getErr, dataLink);
            return new NpcapProbeResult(backend, null);
        }
        catch (Exception ex)
        {
            if (library != nint.Zero)
            {
                try { NativeLibrary.Free(library); } catch { /* 尽力释放 */ }
            }
            return new NpcapProbeResult(null, $"加载 {libraryPath} 失败：{ex.Message}");
        }
    }

    private static string? ResolveLibraryPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            candidates.Add(Path.Combine(windowsDirectory, "System32", "Npcap", "wpcap.dll"));
            candidates.Add(Path.Combine(windowsDirectory, "SysWOW64", "Npcap", "wpcap.dll"));
            candidates.Add(Path.Combine(windowsDirectory, "System32", "wpcap.dll"));
            candidates.Add(Path.Combine(windowsDirectory, "SysWOW64", "wpcap.dll"));
        }
        candidates.Add("wpcap.dll");

        foreach (var candidate in candidates)
        {
            try
            {
                if (!candidate.Contains(Path.DirectorySeparatorChar)) return candidate; // 交给系统搜索
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略单个候选路径异常
            }
        }
        return null;
    }

    private static void PreloadDependencies(string libraryPath)
    {
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(libraryPath);
        }
        catch
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(directory)) return;

        foreach (var dependency in new[] { "Packet.dll" })
        {
            try
            {
                var path = Path.Combine(directory, dependency);
                if (File.Exists(path)) NativeLibrary.Load(path); // 句柄保持到进程结束
            }
            catch
            {
                // 依赖预加载失败时让主库加载自行报错
            }
        }
    }

    private static T GetDelegate<T>(nint library, string name) where T : Delegate
    {
        var pointer = NativeLibrary.GetExport(library, name);
        if (pointer == nint.Zero) throw new EntryPointNotFoundException($"{name} 未导出");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static T? TryGetDelegate<T>(nint library, string name) where T : Delegate
    {
        try
        {
            var pointer = NativeLibrary.GetExport(library, name);
            return pointer == nint.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 设备选择辅助

    private static IEnumerable<NetworkInterface> EnumerateActiveInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(nic => !IsPseudoInterface(nic))
            .OrderByDescending(HasGateway)
            .ThenByDescending(nic => nic.NetworkInterfaceType is
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or
                NetworkInterfaceType.GigabitEthernet);
    }

    private static bool IsPseudoInterface(NetworkInterface nic)
    {
        var text = nic.Name + " " + nic.Description;
        return text.Contains("Npcap Packet Driver", StringComparison.OrdinalIgnoreCase)
               || text.Contains("WFP", StringComparison.OrdinalIgnoreCase)
               || text.Contains("QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Virtual WiFi Filter", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Native WiFi Filter", StringComparison.OrdinalIgnoreCase)
               || text.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
               || text.Contains("VPN", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
               || text.Contains("回环", StringComparison.Ordinal);
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                .Any(gateway => gateway.Address is not null &&
                                !gateway.Address.Equals(System.Net.IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    private static NpcapDevice? MatchDevice(List<NpcapDevice> devices, NetworkInterface nic)
    {
        try
        {
            var id = nic.Id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                var byGuid = devices.FirstOrDefault(device =>
                    device.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
                if (byGuid is not null) return byGuid;
            }

            if (!string.IsNullOrWhiteSpace(nic.Description))
            {
                var byDescription = devices.FirstOrDefault(device =>
                    !string.IsNullOrWhiteSpace(device.Description) &&
                    string.Equals(device.Description, nic.Description, StringComparison.OrdinalIgnoreCase));
                if (byDescription is not null) return byDescription;
            }
        }
        catch
        {
            // 单个网卡匹配失败继续下一个
        }
        return null;
    }

    private static bool IsRejectedDevice(NpcapDevice device)
    {
        var text = device.Name + " " + device.Description;
        if (text.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var keyword in RejectedKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasPreferredKeyword(NpcapDevice device)
    {
        var text = device.Description ?? device.Name;
        foreach (var keyword in PreferredKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly string[] PreferredKeywords =
    {
        "wi-fi", "wifi", "wlan", "wireless", "802.11", "ethernet", "以太网", "无线"
    };

    private static readonly string[] RejectedKeywords =
    {
        "wan miniport", "loopback", "回环", "wi-fi direct", "bluetooth", "蓝牙", "tunnel",
        "vpn", "network monitor", "virtual", "hyper-v", "vmware", "virtualbox", "pseudo", "meta tunnel"
    };

    // ------------------------------------------------------------------ 字符串/缓冲区小工具

    private static void ZeroBuffer(nint buffer)
    {
        for (var index = 0; index < PcapErrBufSize; index++) Marshal.WriteByte(buffer, index, 0);
    }

    private static string ReadAnsi(nint pointer) =>
        pointer == nint.Zero ? string.Empty : Marshal.PtrToStringAnsi(pointer) ?? string.Empty;

    private string ReadPcapError(nint handle)
    {
        try
        {
            var message = ReadAnsi(_getErr(handle)).Trim();
            return message.Length > 0 ? message : "未知 pcap 错误";
        }
        catch
        {
            return "未知 pcap 错误";
        }
    }

    private readonly record struct NpcapProbeResult(NpcapDirectBackend? Backend, string? Error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapFindAllDevs(out nint alldevs, nint errbuf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PcapFreeAllDevs(nint alldevs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PcapOpenLive(
        [MarshalAs(UnmanagedType.LPStr)] string device, int snaplen, int promisc, int toMs, nint errbuf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapNextEx(nint pcap, nint headerOut, nint dataOut);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PcapClose(nint pcap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PcapGetErr(nint pcap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapDataLink(nint pcap);
}

/// <summary>Npcap 抓包设备（name 为 <c>\Device\NPF_{GUID}</c>，description 为友好网卡名）。</summary>
internal sealed record NpcapDevice(string Name, string? Description)
{
    /// <summary>用于 UI 展示的设备名（优先友好描述）。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description!;
}

/// <summary>Npcap 直连抓包结果。</summary>
internal sealed class NpcapCaptureResult
{
    public List<DiscoveryNeighbor> Neighbors { get; } = new();
    public int Frames { get; set; }
    public int LldpFrames { get; set; }
    public int CdpFrames { get; set; }
    public int DataLink { get; set; }
    public string? Error { get; set; }
}
