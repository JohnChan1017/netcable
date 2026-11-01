using System.Runtime.InteropServices;

namespace NetCable.Services.Network;

/// <summary>
/// Npcap rfmon（802.11 monitor mode）后端：复用 <see cref="NpcapDirectBackend"/> 已加载的
/// wpcap.dll 路径与设备枚举，再额外绑定 rfmon 专用 API：
/// <c>pcap_create</c> → <c>pcap_can_set_rfmon</c> → <c>pcap_set_rfmon</c> →
/// <c>pcap_set_snaplen</c> → <c>pcap_set_promisc</c> → <c>pcap_set_timeout</c> →
/// <c>pcap_activate</c> → <c>pcap_datalink</c>，之后用 <c>pcap_next_ex</c> 读取原始 802.11 帧。
/// <para>刻意不改动 <see cref="NpcapDirectBackend"/>（Discovery 抓包依赖它且已验证），
/// 只以只读方式复用其 wpcap 加载结果与设备列表；所有失败都转换为中文原因，不抛异常。</para>
/// </summary>
internal sealed class NpcapRfmonBackend
{
    public const string BackendName = "Npcap monitor mode";

    /// <summary>带 radiotap 头的 802.11 链路类型。</summary>
    public const int DltIeee80211Radio = 127;

    /// <summary>不带 radiotap 的裸 802.11 链路类型。</summary>
    public const int DltIeee80211 = 105;

    private const int PcapErrBufSize = 256;
    private const int Snaplen = 65535;
    private const int Promiscuous = 1;
    private const int ReadTimeoutMs = 200;
    private const int MaxPacketLength = 262_144;

    /// <summary>
    /// Windows 的 <c>struct pcap_pkthdr</c> 为 <c>struct timeval</c>（2 × uint32 = 8 字节）
    /// + caplen(uint32) + len(uint32)，caplen 固定在第 8 字节（与 NpcapDirectBackend 一致）。
    /// </summary>
    private const int PcapHeaderCaptureLengthOffset = 8;

    private readonly NpcapDirectBackend _deviceSource;
    private readonly PcapCreate _create;
    private readonly PcapCanSetRfmon _canSetRfmon;
    private readonly PcapSetRfmon _setRfmon;
    private readonly PcapSetSnaplen _setSnaplen;
    private readonly PcapSetPromisc _setPromisc;
    private readonly PcapSetTimeout _setTimeout;
    private readonly PcapActivate _activate;
    private readonly PcapDataLink _dataLink;
    private readonly PcapNextEx _nextEx;
    private readonly PcapClose _close;
    private readonly PcapGetErr _getErr;

    private NpcapRfmonBackend(
        NpcapDirectBackend deviceSource,
        string libraryPath,
        PcapCreate create,
        PcapCanSetRfmon canSetRfmon,
        PcapSetRfmon setRfmon,
        PcapSetSnaplen setSnaplen,
        PcapSetPromisc setPromisc,
        PcapSetTimeout setTimeout,
        PcapActivate activate,
        PcapDataLink dataLink,
        PcapNextEx nextEx,
        PcapClose close,
        PcapGetErr getErr)
    {
        _deviceSource = deviceSource;
        LibraryPath = libraryPath;
        _create = create;
        _canSetRfmon = canSetRfmon;
        _setRfmon = setRfmon;
        _setSnaplen = setSnaplen;
        _setPromisc = setPromisc;
        _setTimeout = setTimeout;
        _activate = activate;
        _dataLink = dataLink;
        _nextEx = nextEx;
        _close = close;
        _getErr = getErr;
    }

    /// <summary>已加载的 wpcap.dll 完整路径。</summary>
    public string LibraryPath { get; }

    /// <summary>加载 rfmon 后端（仅 Windows）；失败返回 null 与中文原因。</summary>
    public static NpcapRfmonBackend? TryGet(out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "802.11 monitor mode 捕获当前仅支持 Windows（依赖 Npcap 的 rfmon 能力）。";
            return null;
        }

        // 复用已加载的 wpcap.dll 路径；若 Npcap 未安装会在这里拿到可读原因
        var direct = NpcapDirectBackend.TryGet(out var probeError);
        if (direct is null)
        {
            error = probeError ?? "未找到 wpcap.dll（Npcap 未安装）。";
            return null;
        }

        try
        {
            var library = NativeLibrary.Load(direct.LibraryPath);
            return new NpcapRfmonBackend(
                direct,
                direct.LibraryPath,
                GetDelegate<PcapCreate>(library, "pcap_create"),
                GetDelegate<PcapCanSetRfmon>(library, "pcap_can_set_rfmon"),
                GetDelegate<PcapSetRfmon>(library, "pcap_set_rfmon"),
                GetDelegate<PcapSetSnaplen>(library, "pcap_set_snaplen"),
                GetDelegate<PcapSetPromisc>(library, "pcap_set_promisc"),
                GetDelegate<PcapSetTimeout>(library, "pcap_set_timeout"),
                GetDelegate<PcapActivate>(library, "pcap_activate"),
                GetDelegate<PcapDataLink>(library, "pcap_datalink"),
                GetDelegate<PcapNextEx>(library, "pcap_next_ex"),
                GetDelegate<PcapClose>(library, "pcap_close"),
                GetDelegate<PcapGetErr>(library, "pcap_geterr"));
        }
        catch (Exception ex)
        {
            error = $"加载 {direct.LibraryPath} 的 monitor mode 接口失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>枚举 Npcap 抓包设备（复用 Discovery 的已验证实现）。</summary>
    public List<NpcapDevice> GetDevices(out string? error) => _deviceSource.GetDevices(out error);

    /// <summary>
    /// 打开指定设备的 monitor mode 会话。成功返回会话；失败返回 false 与中文原因
    /// （原因会区分“驱动不支持 / 需要管理员权限 / Npcap 未启用 raw 802.11”等）。
    /// </summary>
    public bool TryOpen(NpcapDevice device, out RfmonSession? session, out string? reason)
    {
        session = null;
        reason = null;

        var errbuf = Marshal.AllocHGlobal(PcapErrBufSize);
        nint handle = nint.Zero;

        try
        {
            ZeroBuffer(errbuf);

            // 1) pcap_create
            handle = _create(device.Name, errbuf);
            if (handle == nint.Zero)
            {
                reason = $"pcap_create 打开设备失败：{ReadAnsi(errbuf)}";
                return false;
            }

            // 2) pcap_can_set_rfmon：0 = 该网卡/驱动不支持 monitor mode（本机 Intel 无线网卡 预期走这里）
            var canSetRfmon = _canSetRfmon(handle);
            if (canSetRfmon == 0)
            {
                reason = $"当前网卡/驱动不支持 monitor mode（pcap_can_set_rfmon 返回 0）。适配器：{device.DisplayName}；" +
                         "需要支持 monitor mode 的外置 USB 无线网卡（如 Atheros AR9271 / RTL8812AU），" +
                         "并在安装 Npcap 时勾选 \"Support raw 802.11 traffic (and monitor mode)\"。";
                return false;
            }

            if (canSetRfmon < 0)
            {
                reason = $"pcap_can_set_rfmon 失败：{ReadPcapError(handle)}";
                return false;
            }

            // 3) 打开 rfmon 并设置抓包参数（pcap_set_* 返回 0 表示成功）
            if (_setRfmon(handle, 1) != 0)
            {
                reason = $"pcap_set_rfmon 失败：{ReadPcapError(handle)}。" +
                         "请确认网卡驱动支持 monitor mode，且 Npcap 安装时勾选了 \"Support raw 802.11 traffic (and monitor mode)\"。";
                return false;
            }

            if (!TrySet("pcap_set_snaplen", _setSnaplen(handle, Snaplen), handle, out reason)) return false;
            if (!TrySet("pcap_set_promisc", _setPromisc(handle, Promiscuous), handle, out reason)) return false;
            if (!TrySet("pcap_set_timeout", _setTimeout(handle, ReadTimeoutMs), handle, out reason)) return false;

            // 4) pcap_activate：0 = 成功；>0 = 警告（仍可用）；<0 = 错误
            var activateResult = _activate(handle);
            if (activateResult < 0)
            {
                reason = BuildActivateReason(activateResult, ReadPcapError(handle), device);
                return false;
            }

            // 5) pcap_datalink：monitor mode 必须是 802.11（127 带 radiotap，105 裸 802.11）
            var dataLink = _dataLink(handle);
            if (dataLink is not (DltIeee80211Radio or DltIeee80211))
            {
                reason = $"monitor mode 已激活，但链路类型为 DLT {dataLink}（不是 802.11）。" +
                         "请在安装 Npcap 时勾选 \"Support raw 802.11 traffic (and monitor mode)\"，" +
                         "或更换支持 monitor mode 的无线网卡。";
                return false;
            }

            var headerSlot = Marshal.AllocHGlobal(nint.Size);
            var dataSlot = Marshal.AllocHGlobal(nint.Size);
            session = new RfmonSession(handle, dataLink, device, headerSlot, dataSlot, () => _close(handle));
            handle = nint.Zero; // 所有权移交给会话
            return true;
        }
        catch (Exception ex)
        {
            reason = $"monitor mode 初始化异常：{ex.Message}";
            return false;
        }
        finally
        {
            if (handle != nint.Zero)
            {
                try { _close(handle); } catch { /* 关闭失败不掩盖真实原因 */ }
            }

            Marshal.FreeHGlobal(errbuf);
        }
    }

    /// <summary>读取下一帧；0=超时无帧，1=有帧，-1=抓包错误，-2=EOF（live capture 不会出现）。</summary>
    public RfmonReadResult ReadNextPacket(RfmonSession session)
    {
        try
        {
            Marshal.WriteIntPtr(session.HeaderSlot, nint.Zero);
            Marshal.WriteIntPtr(session.DataSlot, nint.Zero);

            var result = _nextEx(session.Handle, session.HeaderSlot, session.DataSlot);
            switch (result)
            {
                case 0:
                    return RfmonReadResult.Timeout;
                case -2:
                    return RfmonReadResult.EndOfFile;
                case not 1:
                    return RfmonReadResult.Failure(ReadPcapError(session.Handle));
            }

            var header = Marshal.ReadIntPtr(session.HeaderSlot);
            var data = Marshal.ReadIntPtr(session.DataSlot);
            if (header == nint.Zero || data == nint.Zero) return RfmonReadResult.Timeout;

            var captureLength = Marshal.ReadInt32(header, PcapHeaderCaptureLengthOffset);
            if (captureLength <= 0 || captureLength > MaxPacketLength) return RfmonReadResult.Timeout;

            var frame = new byte[captureLength];
            Marshal.Copy(data, frame, 0, captureLength);
            return RfmonReadResult.Packet(frame);
        }
        catch (Exception ex)
        {
            return RfmonReadResult.Failure($"读取 802.11 帧异常：{ex.Message}");
        }
    }

    private static string BuildActivateReason(int code, string pcapError, NpcapDevice device)
    {
        var known = code switch
        {
            -6 => "PCAP_ERROR_RFMON_NOTSUP：网卡/驱动不支持 monitor mode",
            -8 => "PCAP_ERROR_PERM_DENIED：开启 monitor mode 需要管理员权限",
            -9 => "PCAP_ERROR_IFACE_NOT_UP：无线网卡未启用（请先连接 Wi-Fi）",
            -1 => "PCAP_ERROR：monitor mode 会话激活失败",
            _ => $"pcap_activate 返回 {code}"
        };

        var detail = string.IsNullOrWhiteSpace(pcapError) ? string.Empty : $"：{pcapError}";
        return $"{known}{detail}。适配器：{device.DisplayName}；" +
               "若为驱动不支持，请改用支持 monitor mode 的外置 USB 无线网卡。";
    }

    private bool TrySet(string api, int result, nint handle, out string? reason)
    {
        reason = null;
        if (result == 0) return true;

        reason = $"{api} 失败：{ReadPcapError(handle)}（monitor mode 初始化未完成）";
        return false;
    }

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

    private static void ZeroBuffer(nint buffer)
    {
        for (var index = 0; index < PcapErrBufSize; index++) Marshal.WriteByte(buffer, index, 0);
    }

    private static string ReadAnsi(nint pointer) =>
        pointer == nint.Zero ? string.Empty : Marshal.PtrToStringAnsi(pointer) ?? string.Empty;

    private static T GetDelegate<T>(nint library, string name) where T : Delegate
    {
        var pointer = NativeLibrary.GetExport(library, name);
        if (pointer == nint.Zero) throw new EntryPointNotFoundException($"{name} 未导出");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PcapCreate([MarshalAs(UnmanagedType.LPStr)] string device, nint errbuf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapCanSetRfmon(nint pcap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapSetRfmon(nint pcap, int rfmon);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapSetSnaplen(nint pcap, int snaplen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapSetPromisc(nint pcap, int promisc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapSetTimeout(nint pcap, int timeoutMs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapActivate(nint pcap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapDataLink(nint pcap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapNextEx(nint pcap, nint headerOut, nint dataOut);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PcapClose(nint pcap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PcapGetErr(nint pcap);
}

/// <summary>一次 monitor mode 抓包会话；Dispose 时关闭 pcap handle 并退出 monitor mode。</summary>
internal sealed class RfmonSession : IDisposable
{
    private readonly Action _close;
    private bool _disposed;

    internal RfmonSession(
        nint handle,
        int dataLink,
        NpcapDevice device,
        nint headerSlot,
        nint dataSlot,
        Action close)
    {
        Handle = handle;
        DataLink = dataLink;
        Device = device;
        HeaderSlot = headerSlot;
        DataSlot = dataSlot;
        _close = close;
    }

    internal nint Handle { get; }
    internal nint HeaderSlot { get; }
    internal nint DataSlot { get; }

    /// <summary>pcap_datalink 返回值：127 = 带 radiotap，105 = 裸 802.11。</summary>
    public int DataLink { get; }

    /// <summary>抓包的 Npcap 设备。</summary>
    public NpcapDevice Device { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Marshal.FreeHGlobal(HeaderSlot); } catch { /* 尽力释放 */ }
        try { Marshal.FreeHGlobal(DataSlot); } catch { /* 尽力释放 */ }
        try { _close(); } catch { /* 关闭失败忽略（退出 monitor mode 依赖驱动） */ }
    }
}

/// <summary>monitor mode 单次读取结果。</summary>
internal readonly record struct RfmonReadResult(int Status, byte[]? Frame, string? Error)
{
    public const int StatusTimeout = 0;
    public const int StatusPacket = 1;
    public const int StatusError = -1;
    public const int StatusEndOfFile = -2;

    public static RfmonReadResult Timeout => new(StatusTimeout, null, null);

    public static RfmonReadResult EndOfFile => new(StatusEndOfFile, null, null);

    public static RfmonReadResult Packet(byte[] frame) => new(StatusPacket, frame, null);

    public static RfmonReadResult Failure(string? error) => new(StatusError, null, error);
}
