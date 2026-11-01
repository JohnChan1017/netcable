using System.Globalization;
using System.Text;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// 802.11 beacon / probe-response 信息元素（IE）解析结果。
/// </summary>
internal sealed record WifiIeParseResult(
    string Ssid,
    string Security,
    string Cipher,
    string Akm,
    string PhyType,
    string? Vendor,
    IReadOnlyList<KeyValuePair<string, string>> Summary);

/// <summary>
/// 原始 IE 字节解析器（纯函数，不依赖网络/提权，便于单元测试）。
/// 覆盖：SSID、Supported/Extended Rates、DS、TIM、Country、HT、RSN、HT Operation、
/// VHT、Extension(HE/EHT)、厂商自定义（WPA1/WMM/WPS 等）。
/// </summary>
internal static class WifiIeParser
{
    private static readonly Dictionary<string, string> VendorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00:50:F2"] = "Microsoft",
        ["00:0F:AC"] = "IEEE 802.11",
        ["00:10:18"] = "Broadcom",
        ["00:0C:43"] = "MediaTek",
        ["00:03:7F"] = "Qualcomm Atheros",
        ["00:17:F2"] = "Apple",
        ["00:1B:63"] = "Apple",
        ["00:23:DF"] = "Apple",
        ["00:0E:8E"] = "Cisco",
        ["00:1A:A1"] = "Cisco",
        ["00:22:90"] = "Cisco",
        ["00:26:CB"] = "Intel"
    };

    /// <summary>解析一段原始 IE 字节。非法/截断的 IE 直接忽略，绝不抛异常。</summary>
    public static WifiIeParseResult Parse(byte[]? ieBytes, int capabilityRaw, uint nativePhyType)
    {
        var summary = new List<KeyValuePair<string, string>>();
        var vendorDisplays = new List<string>();
        var vendorNames = new List<string>();

        var ssid = string.Empty;
        int? dsChannel = null;
        var hasHt = false;
        var hasVht = false;
        var hasHe = false;
        var hasEht = false;

        var hasRsn = false;
        var hasWpa1 = false;
        string? rsnGroup = null;
        var rsnCiphers = new List<string>();
        var rsnAkms = new List<string>();
        string? wpaGroup = null;
        var wpaCiphers = new List<string>();
        var wpaAkms = new List<string>();

        if (ieBytes is { Length: > 0 })
        {
            var index = 0;
            while (index + 2 <= ieBytes.Length)
            {
                var id = ieBytes[index];
                var length = ieBytes[index + 1];
                index += 2;
                if (index + length > ieBytes.Length) break;

                var data = new ReadOnlySpan<byte>(ieBytes, index, length);
                index += length;

                switch (id)
                {
                    case 0:
                        ssid = DecodeSsid(data);
                        AddSummary(summary, "SSID", ssid);
                        break;

                    case 1:
                        AddSummary(summary, "Supported Rates", FormatRates(data));
                        break;

                    case 3:
                        if (length >= 1)
                        {
                            dsChannel = data[0];
                            AddSummary(summary, "DS Channel", dsChannel.Value.ToString(CultureInfo.InvariantCulture));
                        }

                        break;

                    case 5:
                        if (length >= 2) AddSummary(summary, "TIM", $"DTIM {data[1]}");
                        break;

                    case 7:
                        if (length >= 3)
                        {
                            var country = Encoding.ASCII.GetString(data[..3]).Trim();
                            if (!string.IsNullOrWhiteSpace(country)) AddSummary(summary, "Country", country);
                        }

                        break;

                    case 45:
                        hasHt = true;
                        AddSummary(summary, "HT", "支持");
                        if (length >= 2)
                        {
                            var htCap = (ushort)(data[0] | (data[1] << 8));
                            AddSummary(summary, "HT Width", (htCap & 0x0002) != 0 ? "40 MHz" : "20 MHz");
                        }

                        break;

                    case 48:
                    {
                        hasRsn = true;
                        var suites = ParseSuiteBody(data);
                        rsnGroup = suites.Group;
                        rsnCiphers = suites.Ciphers;
                        rsnAkms = suites.Akms;

                        AddSummary(summary, "RSN 版本", suites.Version.ToString(CultureInfo.InvariantCulture));
                        if (rsnGroup is not null) AddSummary(summary, "RSN Group Cipher", rsnGroup);
                        if (rsnCiphers.Count > 0)
                            AddSummary(summary, "RSN Cipher", string.Join(" / ", rsnCiphers));
                        if (rsnAkms.Count > 0) AddSummary(summary, "AKM", string.Join(" / ", rsnAkms));
                        AddSummary(summary, "PMF", suites.Pmf);
                        break;
                    }

                    case 50:
                        AddSummary(summary, "Extended Rates", FormatRates(data));
                        break;

                    case 61:
                        hasHt = true;
                        AddSummary(summary, "HT Operation", "支持");
                        break;

                    case 191:
                        hasVht = true;
                        AddSummary(summary, "VHT", "支持");
                        if (length >= 4)
                        {
                            var vhtCap = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                            var width = vhtCap & 0x3;
                            AddSummary(summary, "VHT Width", width switch
                            {
                                0 => "20/40 MHz",
                                1 => "80 MHz",
                                2 => "160 MHz",
                                3 => "80+80 MHz",
                                _ => "未知"
                            });
                        }

                        break;

                    case 192:
                        hasVht = true;
                        AddSummary(summary, "VHT Operation", "支持");
                        break;

                    case 221:
                        ParseVendorElement(data, summary, vendorDisplays, vendorNames,
                            ref hasWpa1, ref wpaGroup, wpaCiphers, wpaAkms);
                        break;

                    case 255:
                        ParseExtensionElement(data, summary, ref hasHe, ref hasEht);
                        break;
                }
            }
        }

        if (vendorDisplays.Count > 0)
        {
            AddSummary(summary, "厂商 IE", string.Join(" · ", vendorDisplays));
        }

        var cipher = BuildCipher(hasRsn, rsnGroup, rsnCiphers, hasWpa1, wpaGroup, wpaCiphers);
        var akm = BuildAkm(hasRsn, rsnAkms, hasWpa1, wpaAkms);
        var security = BuildSecurity(hasRsn, hasWpa1, rsnAkms, wpaAkms, capabilityRaw);
        var phyType = InferPhyType(hasEht, hasHe, hasVht, hasHt, nativePhyType);
        var vendor = vendorNames.Count > 0
            ? string.Join(" / ", vendorNames.Distinct(StringComparer.OrdinalIgnoreCase))
            : vendorDisplays.Count > 0
                ? vendorDisplays[0]
                : null;

        // DS 信道与频率换算可能不一致时以 IE 为准（真实 beacon 里 DS 参数是权威信道）
        if (dsChannel is not null) AddSummary(summary, "信道", dsChannel.Value.ToString(CultureInfo.InvariantCulture));

        return new WifiIeParseResult(ssid, security, cipher, akm, phyType, vendor, summary);
    }

    /// <summary>中心频率（MHz）换算信道号：2.4G / 5G / 6G 三套公式。</summary>
    public static int ChannelFromFrequency(uint frequencyMhz)
    {
        var frequency = (int)Math.Min(frequencyMhz, int.MaxValue);
        if (frequency == 2484) return 14;
        if (frequency is >= 2412 and < 2500) return (frequency - 2407) / 5;
        if (frequency is >= 5000 and <= 5925) return (frequency - 5000) / 5;
        if (frequency is >= 5950 and <= 7125) return (frequency - 5950) / 5;
        return 0;
    }

    /// <summary>RSSI(dBm) 换算信号百分比（近似）。</summary>
    public static int RssiToQuality(int rssiDbm) => Math.Clamp((rssiDbm + 100) * 2, 0, 100);

    // ------------------------------------------------------------------ IE 子解析

    private sealed record SuiteResult(
        ushort Version,
        string? Group,
        List<string> Ciphers,
        List<string> Akms,
        string? Pmf);

    private static SuiteResult ParseSuiteBody(ReadOnlySpan<byte> body)
    {
        var ciphers = new List<string>();
        var akms = new List<string>();
        string? pmf = null;

        if (body.Length < 4) return new SuiteResult(0, null, ciphers, akms, pmf);

        var version = ReadU16(body, 0);
        string? group = body.Length >= 6 ? MapCipher(body.Slice(2, 4)) : null;

        var offset = 6;
        if (offset + 2 <= body.Length)
        {
            var pairwiseCount = ReadU16(body, offset);
            offset += 2;
            for (var i = 0; i < pairwiseCount && offset + 4 <= body.Length; i++)
            {
                var cipher = MapCipher(body.Slice(offset, 4));
                if (!string.IsNullOrWhiteSpace(cipher) && !ciphers.Contains(cipher)) ciphers.Add(cipher);
                offset += 4;
            }
        }

        if (offset + 2 <= body.Length)
        {
            var akmCount = ReadU16(body, offset);
            offset += 2;
            for (var i = 0; i < akmCount && offset + 4 <= body.Length; i++)
            {
                var akm = MapAkm(body.Slice(offset, 4));
                if (!string.IsNullOrWhiteSpace(akm) && !akms.Contains(akm)) akms.Add(akm);
                offset += 4;
            }
        }

        if (offset + 2 <= body.Length)
        {
            var capabilities = ReadU16(body, offset);
            if ((capabilities & 0x0080) != 0) pmf = "必需";
            else if ((capabilities & 0x0040) != 0) pmf = "支持";
        }

        return new SuiteResult(version, group, ciphers, akms, pmf);
    }

    private static void ParseVendorElement(
        ReadOnlySpan<byte> data,
        List<KeyValuePair<string, string>> summary,
        List<string> vendorDisplays,
        List<string> vendorNames,
        ref bool hasWpa1,
        ref string? wpaGroup,
        List<string> wpaCiphers,
        List<string> wpaAkms)
    {
        if (data.Length < 3) return;

        var oui = $"{data[0]:X2}:{data[1]:X2}:{data[2]:X2}";
        var type = data.Length >= 4 ? data[3] : -1;
        var display = DescribeVendorElement(oui, type);

        if (!vendorDisplays.Contains(display, StringComparer.OrdinalIgnoreCase)) vendorDisplays.Add(display);

        if (VendorNames.TryGetValue(oui, out var vendorName) &&
            !vendorNames.Contains(vendorName, StringComparer.OrdinalIgnoreCase))
        {
            vendorNames.Add(vendorName);
        }

        if (type < 0) return;

        if (oui == "00:50:F2" && type == 1 && data.Length >= 8)
        {
            // WPA1（Microsoft 厂商 IE 的 type 1）
            hasWpa1 = true;
            var suites = ParseSuiteBody(data[4..]);
            wpaGroup = suites.Group;
            wpaCiphers.Clear();
            wpaCiphers.AddRange(suites.Ciphers);
            wpaAkms.Clear();
            wpaAkms.AddRange(suites.Akms);

            AddSummary(summary, "WPA 版本", suites.Version.ToString(CultureInfo.InvariantCulture));
            if (suites.Group is not null) AddSummary(summary, "WPA Group Cipher", suites.Group);
            if (suites.Ciphers.Count > 0)
                AddSummary(summary, "WPA Cipher", string.Join(" / ", suites.Ciphers));
            if (suites.Akms.Count > 0)
                AddSummary(summary, "WPA AKM", string.Join(" / ", suites.Akms));
            return;
        }

        if (oui == "00:50:F2" && type == 2)
        {
            var subtype = data.Length >= 5 ? data[4] : 0;
            AddSummary(summary, "WMM", subtype == 1 ? "支持（参数集）" : "支持");
            return;
        }

        if (oui == "00:50:F2" && type == 4)
        {
            AddSummary(summary, "WPS", "支持");
            return;
        }

        // 其他厂商 IE：在摘要中保留 OUI:类型，便于排查
        AddSummary(summary, "厂商自定义", display);
    }

    private static string DescribeVendorElement(string oui, int type)
    {
        if (type < 0) return oui;

        var label = (oui, type) switch
        {
            ("00:50:F2", 1) => "WPA",
            ("00:50:F2", 2) => "WMM",
            ("00:50:F2", 4) => "WPS",
            ("00:50:F2", 5) => "WMM-SA",
            ("00:0F:AC", 1) => "WMM",
            ("00:0F:AC", 2) => "WMM-PS",
            _ => null
        };

        var display = $"{oui}:{type:X2}";
        return label is null ? display : $"{display}({label})";
    }

    private static void ParseExtensionElement(
        ReadOnlySpan<byte> data,
        List<KeyValuePair<string, string>> summary,
        ref bool hasHe,
        ref bool hasEht)
    {
        if (data.Length < 1) return;

        switch (data[0])
        {
            case 35: // HE Capabilities
                hasHe = true;
                AddSummary(summary, "HE", "支持");
                break;
            case 36: // HE Operation
                hasHe = true;
                AddSummary(summary, "HE Operation", "支持");
                break;
            case 59: // HE 6 GHz Band Capabilities
                hasHe = true;
                AddSummary(summary, "HE 6GHz", "支持");
                break;
            case 106: // EHT Operation
            case 107: // EHT Capabilities
                hasEht = true;
                AddSummary(summary, "EHT", "支持");
                break;
            case 108: // EHT Multi-Link
                hasEht = true;
                AddSummary(summary, "EHT Multi-Link", "支持");
                break;
        }
    }

    // ------------------------------------------------------------------ 属性映射

    private static string MapCipher(ReadOnlySpan<byte> suite)
    {
        if (suite.Length < 4) return string.Empty;
        var oui = $"{suite[0]:X2}:{suite[1]:X2}:{suite[2]:X2}";
        var type = suite[3];

        if (oui == "00:0F:AC")
        {
            return type switch
            {
                1 => "WEP-40",
                2 => "TKIP",
                4 => "CCMP",
                5 => "WEP-104",
                6 => "BIP-CMAC-128",
                8 => "GCMP-128",
                9 => "GCMP-256",
                10 => "CCMP-256",
                11 => "BIP-GMAC-128",
                12 => "BIP-GMAC-256",
                13 => "BIP-CMAC-256",
                _ => $"{oui}:{type:X2}"
            };
        }

        if (oui == "00:50:F2")
        {
            return type switch
            {
                1 => "WEP-40",
                2 => "TKIP",
                4 => "CCMP",
                5 => "WEP-104",
                _ => $"{oui}:{type:X2}"
            };
        }

        return $"{oui}:{type:X2}";
    }

    private static string MapAkm(ReadOnlySpan<byte> suite)
    {
        if (suite.Length < 4) return string.Empty;
        var oui = $"{suite[0]:X2}:{suite[1]:X2}:{suite[2]:X2}";
        var type = suite[3];

        if (oui == "00:50:F2")
        {
            return type switch
            {
                1 => "802.1X",
                2 => "PSK",
                _ => $"{oui}:{type:X2}"
            };
        }

        if (oui != "00:0F:AC") return $"{oui}:{type:X2}";

        return type switch
        {
            1 => "802.1X",
            2 => "PSK",
            3 => "FT-802.1X",
            4 => "FT-PSK",
            5 => "802.1X-SHA256",
            6 => "PSK-SHA256",
            7 => "TPK",
            8 => "SAE",
            9 => "FT-SAE",
            10 => "AP-PEER-KEY",
            11 => "802.1X-SUITE-B",
            12 => "SUITE-B-192",
            13 => "FILS-SHA256",
            14 => "FILS-SHA384",
            15 => "FT-FILS-SHA256",
            16 => "FT-FILS-SHA384",
            17 => "OWE-TRANSITION",
            18 => "OWE",
            19 => "FT-OWE",
            20 => "OWE-TRANSITION",
            _ => $"{oui}:{type:X2}"
        };
    }

    private static string BuildCipher(
        bool hasRsn,
        string? rsnGroup,
        List<string> rsnCiphers,
        bool hasWpa1,
        string? wpaGroup,
        List<string> wpaCiphers)
    {
        if (hasRsn)
        {
            if (rsnCiphers.Count > 0) return string.Join(" / ", rsnCiphers);
            if (rsnGroup is not null) return rsnGroup;
        }

        if (hasWpa1)
        {
            if (wpaCiphers.Count > 0) return string.Join(" / ", wpaCiphers);
            if (wpaGroup is not null) return wpaGroup;
        }

        return "无";
    }

    private static string BuildAkm(bool hasRsn, List<string> rsnAkms, bool hasWpa1, List<string> wpaAkms)
    {
        if (hasRsn && rsnAkms.Count > 0) return string.Join(" / ", rsnAkms);
        if (hasWpa1 && wpaAkms.Count > 0) return string.Join(" / ", wpaAkms);
        return "无";
    }

    private static string BuildSecurity(
        bool hasRsn,
        bool hasWpa1,
        List<string> rsnAkms,
        List<string> wpaAkms,
        int capabilityRaw)
    {
        var akms = hasRsn ? rsnAkms : hasWpa1 ? wpaAkms : new List<string>();
        var hasSae = akms.Any(a => a is "SAE" or "FT-SAE" or "OWE-TRANSITION");
        var hasOwe = akms.Contains("OWE");
        var hasPsk = akms.Any(a => a is "PSK" or "FT-PSK" or "PSK-SHA256");
        var hasEnterprise = akms.Any(a => a.StartsWith("802.1X", StringComparison.Ordinal) ||
                                          a.StartsWith("SUITE-B", StringComparison.Ordinal) ||
                                          a.StartsWith("FILS", StringComparison.Ordinal) ||
                                          a.StartsWith("FT-802.1X", StringComparison.Ordinal) ||
                                          a.StartsWith("FT-FILS", StringComparison.Ordinal));
        var isWpa3Enterprise = akms.Any(a => a is "SUITE-B-192" or "FILS-SHA384" or "FT-FILS-SHA384");

        if (hasRsn)
        {
            if (hasOwe && !hasPsk && !hasEnterprise) return "OWE";
            if (hasSae && hasPsk) return "WPA3/WPA2-Personal";
            if (hasSae) return "WPA3-Personal";
            if (isWpa3Enterprise) return "WPA3-Enterprise";
            if (hasEnterprise && hasPsk) return "WPA2-Enterprise";
            if (hasEnterprise) return "WPA2-Enterprise";
            if (hasPsk) return "WPA2-Personal";
            return hasWpa1 ? "WPA/WPA2" : "WPA2";
        }

        if (hasWpa1)
        {
            if (hasEnterprise && hasPsk) return "WPA/WPA2-Enterprise";
            if (hasEnterprise) return "WPA-Enterprise";
            if (hasPsk) return "WPA-Personal";
            return "WPA";
        }

        return (capabilityRaw & 0x0010) != 0 ? "WEP" : "开放";
    }

    private static string InferPhyType(
        bool hasEht,
        bool hasHe,
        bool hasVht,
        bool hasHt,
        uint nativePhyType)
    {
        if (hasEht) return "802.11be";
        if (hasHe) return "802.11ax";
        if (hasVht) return "802.11ac";
        if (hasHt) return "802.11n";

        return nativePhyType switch
        {
            4 => "802.11a",
            5 => "802.11b",
            6 => "802.11g",
            7 => "802.11n",
            8 => "802.11ac",
            9 => "802.11ad",
            10 => "802.11ax",
            11 => "802.11be",
            1 or 2 => "802.11",
            _ => "未知"
        };
    }

    // ------------------------------------------------------------------ 基础工具

    /// <summary>解码 SSID 字节（优先 UTF-8，含非法字节时退回 Latin-1）。</summary>
    public static string DecodeSsid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains('\uFFFD')) text = Encoding.Latin1.GetString(bytes);
        return text.TrimEnd('\0');
    }

    private static string FormatRates(ReadOnlySpan<byte> data)
    {
        var rates = new List<string>();
        foreach (var rateByte in data)
        {
            var rate = (rateByte & 0x7F) * 0.5;
            if (rate <= 0) continue;

            var text = rate.ToString("0.#", CultureInfo.InvariantCulture);
            if (!rates.Contains(text)) rates.Add(text);
            if (rates.Count >= 12) break;
        }

        return rates.Count == 0 ? string.Empty : string.Join(", ", rates) + " Mbps";
    }

    private static void AddSummary(List<KeyValuePair<string, string>> summary, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        for (var i = 0; i < summary.Count; i++)
        {
            if (!string.Equals(summary[i].Key, key, StringComparison.Ordinal)) continue;
            if (summary[i].Value.Contains(value, StringComparison.OrdinalIgnoreCase)) return;
            summary[i] = new KeyValuePair<string, string>(key, summary[i].Value + " · " + value);
            return;
        }

        summary.Add(new KeyValuePair<string, string>(key, value));
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));
}
