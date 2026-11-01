using System.Runtime.InteropServices;
using NetCable.Core.Platform;

namespace NetCable.Services.Platform.Internal;

/// <summary>从 WLAN_BSS_ENTRY 中读出的原始 AP 记录（IE 保持原始字节，交给 <see cref="WifiIeParser"/> 解析）。</summary>
internal sealed record NativeBssEntry(
    string Ssid,
    string Bssid,
    int RssiDbm,
    int SignalQuality,
    int BeaconPeriod,
    int CapabilityRaw,
    uint FrequencyMhz,
    uint PhyType,
    int BssType,
    byte[] IeBytes);

/// <summary>WLAN API 读取结果。</summary>
internal sealed record WlanReadOutcome(
    uint ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<NativeBssEntry> Entries,
    int InterfaceCount,
    IReadOnlyList<string> InterfaceDescriptions)
{
    public bool Success => ErrorCode == 0;
}

/// <summary>
/// Windows WLAN API（wlanapi.dll）P/Invoke 封装：
/// WlanOpenHandle(2) → WlanEnumInterfaces → 对每个接口 WlanGetNetworkBssList(null SSID, any, FALSE)。
/// 关键点：<c>WLAN_BSS_LIST</c> 是变长结构（dwNumberOfItems + 变长 WLAN_BSS_ENTRY 数组），
/// 这里同时兼容“定长条目”和“IE 紧跟条目的变长条目”两种布局，并通过字段合理性校验选择正确布局。
/// </summary>
internal static class WifiBssNative
{
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorNotSupported = 50;

    private const uint ClientVersion = 2;      // WLAN_API_VERSION_2_0
    private const uint BssTypeAny = 3;         // dot11_BSS_type_any
    private const int MaxEntriesPerInterface = 4096;
    private const int MaxIeSize = 16384;

    internal static int EntryHeaderSize => Marshal.SizeOf<WLAN_BSS_ENTRY_HEADER>();
    internal static int InterfaceInfoSize => Marshal.SizeOf<WLAN_INTERFACE_INFO>();

    /// <summary>校验 x64 布局是否与 wlanapi.h 一致（供自检/测试使用）。</summary>
    internal static bool LayoutValid()
    {
        try
        {
            return EntryHeaderSize == 360 &&
                   InterfaceInfoSize == 532 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.dot11Ssid)).ToInt32() == 0 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.uPhyId)).ToInt32() == 36 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.dot11Bssid)).ToInt32() == 40 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.dot11BssType)).ToInt32() == 48 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.dot11BssPhyType)).ToInt32() == 52 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.lRssi)).ToInt32() == 56 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.uLinkQuality)).ToInt32() == 60 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.usBeaconPeriod)).ToInt32() == 66 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.ullTimestamp)).ToInt32() == 72 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.usCapabilityInformation)).ToInt32() == 88 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.ulChCenterFrequency)).ToInt32() == 92 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.wlanRateSet)).ToInt32() == 96 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.ulIeOffset)).ToInt32() == 352 &&
                   Marshal.OffsetOf<WLAN_BSS_ENTRY_HEADER>(nameof(WLAN_BSS_ENTRY_HEADER.ulIeSize)).ToInt32() == 356;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取周边 AP 列表。<paramref name="singleInterfaceOnly"/> 为 true 时只查询第一个无线接口
    /// （用于快速探测权限，不做完整读取）。
    /// </summary>
    public static WlanReadOutcome ReadBssList(bool singleInterfaceOnly)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WlanReadOutcome(
                ErrorNotSupported,
                "当前平台不支持 Windows WLAN API",
                Array.Empty<NativeBssEntry>(),
                0,
                Array.Empty<string>());
        }

        var entries = new List<NativeBssEntry>();
        var descriptions = new List<string>();
        var interfaceCount = 0;
        uint firstError = 0;
        string? errorMessage = null;
        var handle = IntPtr.Zero;
        var interfaceList = IntPtr.Zero;

        try
        {
            var result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle);
            if (result != ErrorSuccess) return new WlanReadOutcome(result, null, entries, 0, descriptions);

            result = WlanEnumInterfaces(handle, IntPtr.Zero, out interfaceList);
            if (result != ErrorSuccess) return new WlanReadOutcome(result, null, entries, 0, descriptions);
            if (interfaceList == IntPtr.Zero) return new WlanReadOutcome(0, null, entries, 0, descriptions);

            interfaceCount = Math.Max(0, Marshal.ReadInt32(interfaceList, 0));
            var stride = InterfaceInfoSize;

            for (var i = 0; i < interfaceCount; i++)
            {
                if (singleInterfaceOnly && i > 0) break;

                WLAN_INTERFACE_INFO info;
                try
                {
                    info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(IntPtr.Add(interfaceList, 8 + i * stride));
                }
                catch
                {
                    continue;
                }

                var description = info.strInterfaceDescription ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(description)) descriptions.Add(description);

                var interfaceGuid = info.InterfaceGuid;
                var bssListPtr = IntPtr.Zero;
                try
                {
                    var bssResult = WlanGetNetworkBssList(
                        handle,
                        ref interfaceGuid,
                        IntPtr.Zero,
                        BssTypeAny,
                        false,
                        IntPtr.Zero,
                        out bssListPtr);

                    if (bssResult != ErrorSuccess)
                    {
                        if (firstError == 0) firstError = bssResult;
                        continue;
                    }

                    if (bssListPtr != IntPtr.Zero)
                    {
                        entries.AddRange(ParseBssList(bssListPtr));
                    }
                }
                finally
                {
                    if (bssListPtr != IntPtr.Zero)
                    {
                        try { WlanFreeMemory(bssListPtr); } catch { }
                    }
                }
            }

            // 有数据就视为成功；否则把第一个错误码返回（例如非管理员时的错误 5）
            var effectiveError = entries.Count > 0 ? ErrorSuccess : firstError;
            return new WlanReadOutcome(effectiveError, errorMessage, entries, interfaceCount, descriptions);
        }
        catch (DllNotFoundException ex)
        {
            return new WlanReadOutcome(126, ex.Message, entries, interfaceCount, descriptions);
        }
        catch (EntryPointNotFoundException ex)
        {
            return new WlanReadOutcome(127, ex.Message, entries, interfaceCount, descriptions);
        }
        catch (Exception ex)
        {
            return new WlanReadOutcome(uint.MaxValue, ex.Message, entries, interfaceCount, descriptions);
        }
        finally
        {
            if (interfaceList != IntPtr.Zero)
            {
                try { WlanFreeMemory(interfaceList); } catch { }
            }

            if (handle != IntPtr.Zero)
            {
                try { WlanCloseHandle(handle, IntPtr.Zero); } catch { }
            }
        }
    }

    /// <summary>把 WLAN_BSS_LIST 指针解析成条目列表（自动识别定长/变长布局）。</summary>
    private static List<NativeBssEntry> ParseBssList(IntPtr listPtr)
    {
        var entries = new List<NativeBssEntry>();
        try
        {
            var totalSize = (uint)Marshal.ReadInt32(listPtr, 0);
            var count = Marshal.ReadInt32(listPtr, 4);
            if (count <= 0 || totalSize == 0) return entries;
            if (count > MaxEntriesPerInterface) count = MaxEntriesPerInterface;

            var headerSize = EntryHeaderSize;
            var basePtr = IntPtr.Add(listPtr, 8);

            // 布局 A：WINDOWS 文档中“定长数组”形式（每个条目固定 sizeof(WLAN_BSS_ENTRY)，IE 通过 ulIeOffset 定位）
            var fixedEntries = TryParseFixedLayout(basePtr, listPtr, totalSize, count, headerSize);
            if (fixedEntries is { Count: > 0 } && fixedEntries.Count == count) return fixedEntries;

            // 布局 B：变长条目（IE 紧跟条目头，下一入口 = 本入口 + ulIeOffset + ulIeSize）
            foreach (var alignment in new[] { 8, 4, 2, 1 })
            {
                var variableEntries = TryParseVariableLayout(listPtr, totalSize, count, headerSize, alignment);
                if (variableEntries is { Count: > 0 } && variableEntries.Count == count) return variableEntries;
            }

            return fixedEntries ?? entries;
        }
        catch
        {
            return entries;
        }
    }

    private static List<NativeBssEntry>? TryParseFixedLayout(
        IntPtr basePtr,
        IntPtr listPtr,
        uint totalSize,
        int count,
        int headerSize)
    {
        var entries = new List<NativeBssEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var entryPtr = IntPtr.Add(basePtr, i * headerSize);
            var offsetFromList = 8L + (long)i * headerSize;
            if (!TryReadEntry(entryPtr, listPtr, offsetFromList, totalSize, out var entry, out _, out _))
            {
                return null;
            }

            entries.Add(entry!);
        }

        return entries;
    }

    private static List<NativeBssEntry>? TryParseVariableLayout(
        IntPtr listPtr,
        uint totalSize,
        int count,
        int headerSize,
        int alignment)
    {
        var entries = new List<NativeBssEntry>(count);
        long position = 8;

        for (var i = 0; i < count; i++)
        {
            if (position + headerSize > totalSize) return null;

            var entryPtr = IntPtr.Add(listPtr, (int)position);
            if (!TryReadEntry(entryPtr, listPtr, position, totalSize, out var entry, out var ieOffset, out var ieSize))
            {
                return null;
            }

            entries.Add(entry!);
            position += Math.Max(headerSize, ieOffset) + ieSize;
            position = AlignUp(position, alignment);
        }

        return entries;
    }

    private static bool TryReadEntry(
        IntPtr entryPtr,
        IntPtr listPtr,
        long offsetFromList,
        uint totalSize,
        out NativeBssEntry? entry,
        out uint ieOffset,
        out uint ieSize)
    {
        entry = null;
        ieOffset = 0;
        ieSize = 0;

        WLAN_BSS_ENTRY_HEADER header;
        try
        {
            header = Marshal.PtrToStructure<WLAN_BSS_ENTRY_HEADER>(entryPtr);
        }
        catch
        {
            return false;
        }

        var ssidLength = header.dot11Ssid.uSSIDLength;
        if (ssidLength > 32) return false;

        var ssidBytes = header.dot11Ssid.ucSSID;
        ssidLength = Math.Min(ssidLength, (uint)(ssidBytes?.Length ?? 0));
        var ssid = WifiIeParser.DecodeSsid(
            ssidBytes is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(ssidBytes, 0, (int)ssidLength));

        var bssidBytes = header.dot11Bssid;
        if (bssidBytes is null || bssidBytes.Length < 6) return false;
        if (bssidBytes.All(b => b == 0)) return false;

        var frequency = header.ulChCenterFrequency;
        if (frequency is < 2400 or > 7200) return false;

        var rssi = header.lRssi;
        if (rssi is < -120 or > 20) return false;

        var bssType = (int)header.dot11BssType;
        if (bssType is < 1 or > 3) return false;

        ieOffset = header.ulIeOffset;
        ieSize = header.ulIeSize;

        if (ieSize > MaxIeSize) return false;
        if (ieSize > 0)
        {
            var ieAbsolute = offsetFromList + ieOffset;
            if (ieOffset < 352 || ieAbsolute + ieSize > totalSize) return false;
        }

        var ieBytes = Array.Empty<byte>();
        if (ieSize > 0)
        {
            ieBytes = new byte[ieSize];
            try
            {
                Marshal.Copy(IntPtr.Add(entryPtr, (int)ieOffset), ieBytes, 0, (int)ieSize);
            }
            catch
            {
                return false;
            }
        }

        var bssid = string.Join(':', bssidBytes.Take(6).Select(b => b.ToString("x2")));
        var quality = (int)Math.Clamp((long)header.uLinkQuality, 0, 100);
        if (quality == 0) quality = WifiIeParser.RssiToQuality(rssi);

        entry = new NativeBssEntry(
            ssid,
            bssid,
            rssi,
            quality,
            header.usBeaconPeriod,
            header.usCapabilityInformation,
            frequency,
            header.dot11BssPhyType,
            bssType,
            ieBytes);

        return true;
    }

    private static long AlignUp(long value, int alignment)
    {
        if (alignment <= 1) return value;
        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    // ------------------------------------------------------------------ 原生结构

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_RATE_SET
    {
        public uint uRateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public ushort[] usRateSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_ENTRY_HEADER
    {
        public DOT11_SSID dot11Ssid;
        public uint uPhyId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public uint dot11BssType;
        public uint dot11BssPhyType;
        public int lRssi;
        public uint uLinkQuality;
        public byte bInRegDomain;
        public ushort usBeaconPeriod;
        public ulong ullTimestamp;
        public ulong ullHostTimestamp;
        public ushort usCapabilityInformation;
        public uint ulChCenterFrequency;
        public WLAN_RATE_SET wlanRateSet;
        public uint ulIeOffset;
        public uint ulIeSize;
    }

    // ------------------------------------------------------------------ P/Invoke

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanOpenHandle(
        uint dwClientVersion,
        IntPtr pReserved,
        out uint pdwNegotiatedVersion,
        out IntPtr phClientHandle);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle,
        IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern uint WlanGetNetworkBssList(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid,
        uint dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled,
        IntPtr pReserved,
        out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll", SetLastError = false)]
    private static extern void WlanFreeMemory(IntPtr pMemory);
}
