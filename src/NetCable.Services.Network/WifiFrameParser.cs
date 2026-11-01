using System.Text;
using NetCable.Core.Models;

namespace NetCable.Services.Network;

/// <summary>
/// 802.11 原始帧解析器（monitor mode）。
/// <para>支持两条链路类型：<c>DLT_IEEE802_11_RADIO</c>(127) 先解析 radiotap 头再解析 802.11；
/// <c>DLT_IEEE802_11</c>(105) 直接解析 802.11。radiotap 字段按 present bitmap 的位顺序遍历，
/// 因此 TSFT/Flags/Rate/Channel/Antenna signal 出现在任意组合时都能正确定位。</para>
/// <para>802.11 头按 ToDS/FromDS 组合映射 Destination/Source/BSSID；管理帧按 subtype 映射名称，
/// 并对 Beacon / Probe Response / Probe Request / Association Request 等解析 SSID(0)、DS Param(3)、
/// RSN(48)、HT(45)/VHT(191) 信息元素，产出可读备注。</para>
/// </summary>
internal static class WifiFrameParser
{
    public const string ManagementFrameType = "管理帧";
    public const string ControlFrameType = "控制帧";
    public const string DataFrameType = "数据帧";
    public const string ExtensionFrameType = "扩展帧";

    public const int DltIeee80211Radio = 127;
    public const int DltIeee80211 = 105;

    /// <summary>
    /// 解析一帧原始字节；数据不足 / 链路类型不支持 / 头部非法时返回 null（调用方跳过该帧）。
    /// 控制帧与数据帧也返回 <see cref="WifiFrameInfo"/>，由 <see cref="WifiMonitorService"/> 只保留管理帧。
    /// </summary>
    public static WifiFrameInfo? Parse(byte[] packet, int dataLinkType)
    {
        if (packet is null || packet.Length < 10) return null;

        var offset = 0;
        RadiotapInfo? radiotap = null;

        if (dataLinkType == DltIeee80211Radio)
        {
            if (!TryParseRadiotap(packet, out radiotap, out offset)) return null;
        }
        else if (dataLinkType != DltIeee80211)
        {
            return null;
        }

        var frameLength = packet.Length - offset;

        // radiotap Flags 的 bit4 表示帧尾带 4 字节 FCS；解析前去掉，避免 IE 解析读到 FCS 噪声
        if (radiotap is { FcsIncluded: true } && frameLength >= 4) frameLength -= 4;

        if (frameLength < 10) return null;

        var frame = new ReadOnlySpan<byte>(packet, offset, frameLength);
        return ParseDot11(frame, radiotap);
    }

    private static WifiFrameInfo? ParseDot11(ReadOnlySpan<byte> frame, RadiotapInfo? radiotap)
    {
        var frameControl = ReadU16(frame, 0);
        if ((frameControl & 0x0003) != 0) return null; // 协议版本非 0，跳过

        var type = (frameControl >> 2) & 0x0003;
        var subtype = (frameControl >> 4) & 0x000F;
        var toDs = (frameControl & 0x0100) != 0;
        var fromDs = (frameControl & 0x0200) != 0;
        var protectedFrame = (frameControl & 0x4000) != 0;

        var frameType = type switch
        {
            0 => ManagementFrameType,
            1 => ControlFrameType,
            2 => DataFrameType,
            _ => ExtensionFrameType
        };
        var subType = SubTypeName(type, subtype);

        // 控制帧头只有 10 字节（FC + Duration + RA）
        if (type == 1)
        {
            var receiver = FormatMac(frame.Slice(4, 6));
            return new WifiFrameInfo(frameType, subType, string.Empty, receiver, string.Empty,
                null, radiotap?.Channel, radiotap?.RssiDbm, null, JoinDetail(radiotap?.Summary));
        }

        if (frame.Length < 24) return null;

        var address1 = FormatMac(frame.Slice(4, 6));
        var address2 = FormatMac(frame.Slice(10, 6));
        var address3 = FormatMac(frame.Slice(16, 6));
        var sequenceControl = ReadU16(frame, 22);
        var sequence = (long)(sequenceControl >> 4);
        var fragment = sequenceControl & 0x000F;

        var detail = new List<string>();
        if (!string.IsNullOrEmpty(radiotap?.Summary)) detail.Add(radiotap!.Summary);
        if (fragment != 0) detail.Add($"frag={fragment}");

        string destination;
        string source;
        string bssid;
        int headerLength = 24;

        if (type == 0)
        {
            // 管理帧：ToDS/FromDS 恒为 0，DA=Addr1、SA=Addr2、BSSID=Addr3
            destination = address1;
            source = address2;
            bssid = address3;
        }
        else
        {
            // 数据帧按 ToDS/FromDS 四位组合映射
            if (!toDs && !fromDs)
            {
                destination = address1;
                source = address2;
                bssid = address3;
            }
            else if (toDs && !fromDs)
            {
                destination = address3;
                source = address2;
                bssid = address1;
            }
            else if (!toDs && fromDs)
            {
                destination = address1;
                source = address3;
                bssid = address2;
            }
            else
            {
                // WDS 四地址帧：DA=Addr3、SA=Addr4、RA=Addr1（无 BSSID 概念）
                if (frame.Length < 30) return null;
                var address4 = FormatMac(frame.Slice(24, 6));
                destination = address3;
                source = address4;
                bssid = address1;
                headerLength = 30;
                detail.Add("WDS 四地址");
            }

            // QoS Data 在 24/30 字节头之后还有 2 字节 QoS Control（仅影响 IE 偏移，数据帧不解析 IE）
            if ((subtype & 0x08) != 0 && frame.Length >= headerLength + 2) headerLength += 2;
        }

        if (type == 0 && protectedFrame) detail.Add("已加密");

        int? channel = radiotap?.Channel;
        int? rssi = radiotap?.RssiDbm;
        string? ssid = null;

        if (type == 0 && !protectedFrame)
        {
            (ssid, channel) = ParseManagementBody(frame, subtype, headerLength, channel, detail);
        }

        return new WifiFrameInfo(frameType, subType, source, destination, bssid, ssid, channel, rssi,
            sequence, JoinDetail(detail));
    }

    /// <summary>解析管理帧 body 的固定字段与信息元素；返回 SSID 与（若 radiotap 无信道时）DS 信道。</summary>
    private static (string? Ssid, int? Channel) ParseManagementBody(
        ReadOnlySpan<byte> frame,
        int subtype,
        int headerLength,
        int? channel,
        List<string> detail)
    {
        string? ssid = null;

        // Beacon / Probe Response 固定 body：Timestamp(8) + BeaconInterval(2) + Capability(2)
        if (subtype is 5 or 8 && frame.Length >= 36)
        {
            var beaconInterval = ReadU16(frame, 32);
            var capability = ReadU16(frame, 34);
            detail.Add($"beacon={beaconInterval}ms");
            if ((capability & 0x0010) != 0) detail.Add("加密网络");
        }

        var ieOffset = GetIeOffset(subtype);
        if (ieOffset >= 0 && frame.Length > ieOffset)
        {
            ParseInformationElements(frame[ieOffset..], out var ieSsid, out var dsChannel, out var rsn,
                out var ht, out var vht);

            ssid = ieSsid;
            if (dsChannel is not null)
            {
                detail.Add($"DS ch={dsChannel}");
                channel ??= dsChannel;
            }

            if (!string.IsNullOrEmpty(rsn)) detail.Add(rsn);
            if (ht) detail.Add("HT");
            if (vht) detail.Add("VHT");
        }

        // 其他管理帧的固定 body 字段（便于排查 deauth / auth 失败）
        if (subtype is 10 or 12 && frame.Length >= 26)
            detail.Add($"reason={ReadU16(frame, 24)}");
        else if (subtype == 11 && frame.Length >= 30)
            detail.Add($"auth alg={ReadU16(frame, 24)} seq={ReadU16(frame, 26)} status={ReadU16(frame, 28)}");

        return (ssid, channel);
    }

    /// <summary>各管理帧 subtype 的 IE 起始偏移（相对帧首）；-1 表示该 subtype 不解析 IE。</summary>
    private static int GetIeOffset(int subtype) => subtype switch
    {
        0 => 24 + 4,   // Association Request：Capability(2) + ListenInterval(2)
        1 => 24 + 6,   // Association Response：Capability(2) + Status(2) + AID(2)
        2 => 24 + 10,  // Reassociation Request：Capability(2) + ListenInterval(2) + CurrentAP(6)
        3 => 24 + 6,   // Reassociation Response
        4 => 24,       // Probe Request
        5 => 24 + 12,  // Probe Response：Timestamp(8) + BeaconInterval(2) + Capability(2)
        8 => 24 + 12,  // Beacon
        _ => -1
    };

    private static void ParseInformationElements(
        ReadOnlySpan<byte> elements,
        out string? ssid,
        out int? dsChannel,
        out string? rsn,
        out bool ht,
        out bool vht)
    {
        ssid = null;
        dsChannel = null;
        rsn = null;
        ht = false;
        vht = false;

        var offset = 0;
        while (offset + 2 <= elements.Length)
        {
            var id = elements[offset];
            var length = elements[offset + 1];
            offset += 2;

            if (offset + length > elements.Length) break; // 截断或尾部 FCS 噪声
            var value = elements.Slice(offset, length);
            offset += length;

            switch (id)
            {
                case 0 when ssid is null: // SSID
                    ssid = DecodeSsid(value);
                    break;
                case 3 when length == 1: // DS Parameter Set
                    dsChannel = value[0];
                    break;
                case 48: // RSN
                    rsn ??= ParseRsn(value);
                    break;
                case 45: // HT Capabilities
                    ht = true;
                    break;
                case 191: // VHT Capabilities
                    vht = true;
                    break;
            }
        }
    }

    private static string? DecodeSsid(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0) return null; // 隐藏 SSID

        var printable = true;
        foreach (var b in value)
        {
            if (b is < 0x20 or > 0x7E)
            {
                printable = false;
                break;
            }
        }

        if (printable) return Encoding.ASCII.GetString(value);

        try
        {
            var text = new UTF8Encoding(false, true).GetString(value);
            return text.Length > 0 ? text : "0x" + Convert.ToHexString(value);
        }
        catch
        {
            return "0x" + Convert.ToHexString(value);
        }
    }

    /// <summary>解析 RSN IE：组播/单播 cipher 套件 + AKM 套件，产出如 <c>RSN 单播=CCMP, AKM=PSK</c>。</summary>
    private static string? ParseRsn(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2) return null;

        var offset = 2; // RSN Version

        string? group = null;
        if (offset + 4 <= value.Length)
        {
            group = CipherSuiteName(value.Slice(offset, 4));
            offset += 4;
        }

        string? pairwise = null;
        if (offset + 2 <= value.Length)
        {
            var count = ReadU16(value, offset);
            offset += 2;

            var names = new List<string>();
            for (var i = 0; i < count && offset + 4 <= value.Length; i++)
            {
                names.Add(CipherSuiteName(value.Slice(offset, 4)));
                offset += 4;
            }

            if (names.Count > 0) pairwise = string.Join("/", names);
        }

        string? akm = null;
        if (offset + 2 <= value.Length)
        {
            var count = ReadU16(value, offset);
            offset += 2;

            var names = new List<string>();
            for (var i = 0; i < count && offset + 4 <= value.Length; i++)
            {
                names.Add(AkmSuiteName(value.Slice(offset, 4)));
                offset += 4;
            }

            if (names.Count > 0) akm = string.Join("/", names);
        }

        if (group is null && pairwise is null && akm is null) return null;

        var parts = new List<string>();
        if (group is not null) parts.Add($"组播={group}");
        if (pairwise is not null) parts.Add($"单播={pairwise}");
        if (akm is not null) parts.Add($"AKM={akm}");
        return "RSN " + string.Join(", ", parts);
    }

    private static string CipherSuiteName(ReadOnlySpan<byte> suite)
    {
        var oui = $"{suite[0]:X2}:{suite[1]:X2}:{suite[2]:X2}";
        var type = suite[3];

        if (oui == "00:0F:AC")
        {
            return type switch
            {
                0 => "UseGroup",
                1 => "WEP40",
                2 => "TKIP",
                4 => "CCMP",
                5 => "WEP104",
                8 => "GCMP",
                9 => "GCMP-256",
                10 => "CCMP-256",
                _ => $"{oui}:{type:X2}"
            };
        }

        return $"{oui}:{type:X2}";
    }

    private static string AkmSuiteName(ReadOnlySpan<byte> suite)
    {
        var oui = $"{suite[0]:X2}:{suite[1]:X2}:{suite[2]:X2}";
        var type = suite[3];

        if (oui == "00:0F:AC")
        {
            return type switch
            {
                1 => "802.1X",
                2 => "PSK",
                3 => "FT-802.1X",
                4 => "FT-PSK",
                5 => "802.1X-SHA256",
                6 => "PSK-SHA256",
                8 => "SAE",
                9 => "FT-SAE",
                18 => "OWE",
                _ => $"{oui}:{type:X2}"
            };
        }

        return $"{oui}:{type:X2}";
    }

    // ---------------------------------------------------------------- radiotap

    private static bool TryParseRadiotap(ReadOnlySpan<byte> packet, out RadiotapInfo? info, out int payloadOffset)
    {
        info = null;
        payloadOffset = 0;

        if (packet.Length < 8 || packet[0] != 0) return false; // version 必须为 0

        var headerLength = ReadU16(packet, 2);
        if (headerLength < 8 || headerLength > packet.Length) return false;

        var present = ReadU32(packet, 4);
        var wordOffset = 8;
        var wordCount = 1;
        var word = present;

        // 扩展 present bitmap：位31 置位表示后面还有 4 字节 present word
        while ((word & 0x8000_0000u) != 0)
        {
            if (wordCount >= 4 || wordOffset + 4 > headerLength) return false;
            word = ReadU32(packet, wordOffset);
            wordOffset += 4;
            wordCount++;
        }

        var fieldOffset = wordOffset;
        int? frequencyMhz = null;
        int? channel = null;
        int? rssi = null;
        double? rate = null;
        byte? flags = null;

        // 标准字段只在第一个 present word（bit 0..28）；按位顺序遍历并逐字段对齐
        for (var bit = 0; bit <= 28; bit++)
        {
            if ((present & (1u << bit)) == 0) continue;

            var (align, size) = RadiotapFieldLayout(bit);
            if (align == 0) break; // 未知字段：后续字段位置不可知，停止（802.11 起点仍由 it_len 保证）

            fieldOffset = Align(fieldOffset, align);
            if (fieldOffset + size > headerLength) break;

            var field = packet.Slice(fieldOffset, size);
            switch (bit)
            {
                case 1: // Flags
                    flags = field[0];
                    break;
                case 2: // Rate（单位 500 kbps）
                    rate = field[0] * 0.5;
                    break;
                case 3: // Channel：u16 frequency + u16 flags
                    frequencyMhz = ReadU16(field, 0);
                    channel = FrequencyToChannel(frequencyMhz.Value);
                    break;
                case 5: // dBm Antenna Signal（有符号）
                    rssi = (sbyte)field[0];
                    break;
                case 12: // dB Antenna Signal（无符号，仅作兜底）
                    rssi ??= field[0];
                    break;
            }

            fieldOffset += size;
        }

        payloadOffset = headerLength;

        var summaryParts = new List<string>(4);
        if (channel is not null)
            summaryParts.Add(frequencyMhz is null ? $"ch={channel}" : $"ch={channel} ({frequencyMhz} MHz)");
        else if (frequencyMhz is not null)
            summaryParts.Add($"{frequencyMhz} MHz");
        if (rssi is not null) summaryParts.Add($"RSSI={rssi} dBm");
        if (rate is not null) summaryParts.Add($"rate={rate.Value:0.#} Mbps");
        if (flags is { } flagValue && (flagValue & 0x10) != 0) summaryParts.Add("含FCS");

        info = new RadiotapInfo(headerLength, frequencyMhz, channel, rssi, rate, flags,
            string.Join(" · ", summaryParts));
        return true;
    }

    /// <summary>radiotap 标准字段的（对齐, 长度）；未知字段返回 (0,0)。</summary>
    private static (int Align, int Size) RadiotapFieldLayout(int bit) => bit switch
    {
        0 => (8, 8),   // TSFT
        1 => (1, 1),   // Flags
        2 => (1, 1),   // Rate
        3 => (2, 4),   // Channel
        4 => (2, 2),   // FHSS
        5 => (1, 1),   // dBm Antenna Signal
        6 => (1, 1),   // dBm Antenna Noise
        7 => (2, 2),   // Lock Quality
        8 => (2, 2),   // TX Attenuation
        9 => (2, 2),   // dB TX Attenuation
        10 => (1, 1),  // dBm TX Power
        11 => (1, 1),  // Antenna
        12 => (1, 1),  // dB Antenna Signal
        13 => (1, 1),  // dB Antenna Noise
        14 => (2, 2),  // RX Flags
        15 => (2, 2),  // TX Flags
        16 => (1, 1),  // RTS Retries
        17 => (1, 1),  // Data Retries
        18 => (4, 8),  // XChannel
        19 => (1, 3),  // MCS
        20 => (4, 8),  // AMPDU Status
        21 => (2, 12), // VHT
        22 => (8, 12), // Timestamp
        23 => (2, 12), // HE
        24 => (2, 12), // HE-MU
        25 => (1, 1),  // 0-length PSDU
        26 => (4, 4),  // L-SIG
        _ => (0, 0)
    };

    /// <summary>把频率（MHz）换算成信道号：2.4G / 5G / 6G 三套公式。</summary>
    private static int? FrequencyToChannel(int frequencyMhz)
    {
        if (frequencyMhz == 2484) return 14;
        if (frequencyMhz is >= 2412 and <= 2472) return (frequencyMhz - 2407) / 5;
        if (frequencyMhz is >= 5000 and <= 5925) return (frequencyMhz - 5000) / 5;
        if (frequencyMhz is >= 5955 and <= 7125) return (frequencyMhz - 5950) / 5;
        return null;
    }

    // ---------------------------------------------------------------- 小工具

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static int Align(int offset, int alignment) => (offset + alignment - 1) & ~(alignment - 1);

    private static string FormatMac(ReadOnlySpan<byte> value) =>
        value.Length < 6
            ? string.Empty
            : $"{value[0]:X2}:{value[1]:X2}:{value[2]:X2}:{value[3]:X2}:{value[4]:X2}:{value[5]:X2}";

    private static string? JoinDetail(string? first)
        => string.IsNullOrWhiteSpace(first) ? null : first;

    private static string? JoinDetail(List<string> parts)
    {
        if (parts.Count == 0) return null;
        var text = string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return text.Length == 0 ? null : text;
    }

    /// <summary>radiotap 解析结果。</summary>
    internal sealed record RadiotapInfo(
        int HeaderLength,
        int? FrequencyMhz,
        int? Channel,
        int? RssiDbm,
        double? RateMbps,
        byte? Flags,
        string Summary)
    {
        /// <summary>radiotap Flags bit4 = 帧尾包含 4 字节 FCS。</summary>
        public bool FcsIncluded => Flags is { } value && (value & 0x10) != 0;
    }

    /// <summary>802.11 管理帧 subtype → 名称。</summary>
    private static string SubTypeName(int type, int subtype) => type switch
    {
        0 => subtype switch
        {
            0 => "Association Request",
            1 => "Association Response",
            2 => "Reassociation Request",
            3 => "Reassociation Response",
            4 => "Probe Request",
            5 => "Probe Response",
            6 => "Timing Advertisement",
            8 => "Beacon",
            9 => "ATIM",
            10 => "Disassociation",
            11 => "Authentication",
            12 => "Deauthentication",
            13 => "Action",
            14 => "Action No Ack",
            _ => $"Management {subtype}"
        },
        1 => subtype switch
        {
            2 => "Trigger",
            3 => "Tack",
            4 => "Beamforming Report Poll",
            5 => "VHT NDP Announcement",
            6 => "Control Frame Extension",
            7 => "Control Wrapper",
            8 => "Block Ack Request",
            9 => "Block Ack",
            10 => "PS-Poll",
            11 => "RTS",
            12 => "CTS",
            13 => "ACK",
            14 => "CF-End",
            15 => "CF-End + CF-Ack",
            _ => $"Control {subtype}"
        },
        2 => subtype switch
        {
            0 => "Data",
            1 => "Data + CF-Ack",
            2 => "Data + CF-Poll",
            3 => "Data + CF-Ack + CF-Poll",
            4 => "Null",
            5 => "CF-Ack",
            6 => "CF-Poll",
            7 => "CF-Ack + CF-Poll",
            8 => "QoS Data",
            _ => $"Data {subtype}"
        },
        _ => $"Extension {subtype}"
    };
}
