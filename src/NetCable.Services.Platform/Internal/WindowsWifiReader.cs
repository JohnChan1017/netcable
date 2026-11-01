using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Windows WiFi 读取器，解析 <c>netsh wlan show interfaces</c> 与
/// <c>netsh wlan show networks mode=bssid</c>。
/// Windows 11 24H2 起这些命令可能需要管理员权限，无法读取时会返回可展示的原因，不抛异常。
/// </summary>
internal sealed class WindowsWifiReader
{
    private const int TimeoutMs = 12000;

    private static readonly Regex KeyValueRegex = new(
        @"^\s*(?<key>[^:]+?)\s+:\s?(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SsidLineRegex = new(
        @"^SSID\s+\d+\s*:\s*(?<ssid>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BssidLineRegex = new(
        @"^BSSID\s+\d+\s*:\s*(?<bssid>[0-9a-fA-F:]{17})\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string? _interfacesText;
    private bool _interfacesFetched;

    /// <summary>读取 <c>netsh wlan show interfaces</c> 的原始输出（进程内缓存一次）。</summary>
    public async Task<string?> GetInterfacesTextAsync(CancellationToken cancellationToken)
    {
        if (_interfacesFetched) return _interfacesText;
        _interfacesFetched = true;

        try
        {
            var result = await Commanding
                .RunAsync("netsh", new[] { "wlan", "show", "interfaces" }, TimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            var text = result.CombinedOutput;
            _interfacesText = string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _interfacesFetched = false;
            throw;
        }
        catch
        {
            _interfacesText = null;
        }

        return _interfacesText;
    }

    /// <summary>读取 <c>netsh wlan show networks mode=bssid</c> 的原始输出（不缓存，扫描应刷新）。</summary>
    public async Task<string?> GetNetworksTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await Commanding
                .RunAsync("netsh", new[] { "wlan", "show", "networks", "mode=bssid" }, TimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            var text = result.CombinedOutput;
            return string.IsNullOrWhiteSpace(text) ? null : text;
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

    /// <summary>识别“无硬件 / 无权限 / 服务未启动”等不可用原因，可用时返回 null。</summary>
    public static string? DetectUnavailableReason(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("requires elevation", StringComparison.Ordinal) ||
            lower.Contains("run as administrator", StringComparison.Ordinal) ||
            text.Contains("需要提升", StringComparison.Ordinal) ||
            text.Contains("以管理员身份运行", StringComparison.Ordinal))
        {
            return "需要管理员权限：Windows 11 24H2 起读取 WLAN 信息需要提升权限";
        }

        if (lower.Contains("no wireless interface", StringComparison.Ordinal) ||
            text.Contains("没有无线接口", StringComparison.Ordinal) ||
            text.Contains("系统上没有无线", StringComparison.Ordinal))
        {
            return "系统上没有可用的无线网卡";
        }

        if (lower.Contains("wlansvc", StringComparison.Ordinal) ||
            lower.Contains("wireless autoconfig service", StringComparison.Ordinal) ||
            text.Contains("无线自动配置服务", StringComparison.Ordinal) ||
            text.Contains("WLAN AutoConfig", StringComparison.Ordinal))
        {
            return "WLAN AutoConfig (wlansvc) 服务未运行";
        }

        if (lower.Contains("not running", StringComparison.Ordinal) && lower.Contains("service", StringComparison.Ordinal))
        {
            return "WLAN 服务未运行";
        }

        return null;
    }

    /// <summary>解析当前已连接的 WiFi（未连接返回 null）。</summary>
    public static WifiNetworkInfo? ParseCurrent(string text)
    {
        foreach (var block in SplitInterfaceBlocks(text))
        {
            var ssid = GetValue(block, "ssid");
            if (string.IsNullOrWhiteSpace(ssid)) continue;

            var state = GetValue(block, "state", "状态");
            var connected = state is null ||
                            state.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                            state.Contains("已连接", StringComparison.Ordinal);
            if (!connected) continue;

            var channel = PlatformText.ExtractFirstInteger(GetValue(block, "channel", "通道", "信道"));
            var band = GetValue(block, "band", "频带", "频段")
                       ?? PlatformText.BandFromChannel(channel ?? 0);

            var authentication = GetValue(block, "authentication", "身份验证");
            var cipher = GetValue(block, "cipher", "密码", "加密", "加密方式");
            var security = BuildSecurity(authentication, cipher);

            var receiveRate = GetValue(block, "receiverate(mbps)", "receiverate", "接收速率(mbps)", "接收速率");
            var transmitRate = GetValue(block, "transmitrate(mbps)", "transmitrate", "传输速率(mbps)", "传输速率");

            return new WifiNetworkInfo(
                ssid.Trim(),
                PlatformText.NormalizeMac(GetValue(block, "bssid")),
                PlatformText.ParseSignalPercent(GetValue(block, "signal", "信号")),
                channel,
                band,
                security,
                BuildRate(receiveRate, transmitRate),
                true);
        }

        return null;
    }

    /// <summary>解析周边 AP 扫描结果（每个 BSSID 一条记录）。</summary>
    public static IReadOnlyList<WifiNetworkInfo> ParseNetworks(string text, WifiNetworkInfo? current)
    {
        var result = new List<WifiNetworkInfo>();

        string? ssid = null;
        var ssidFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bssidBlocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? bssidFields = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(ssid))
            {
                if (bssidBlocks.Count == 0) bssidBlocks.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

                foreach (var fields in bssidBlocks)
                {
                    MergeInto(fields, ssidFields);
                    result.Add(BuildNetwork(ssid, fields, current));
                }
            }

            ssid = null;
            ssidFields.Clear();
            bssidBlocks.Clear();
            bssidFields = null;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var ssidMatch = SsidLineRegex.Match(trimmed);
            if (ssidMatch.Success)
            {
                Flush();
                ssid = ssidMatch.Groups["ssid"].Value.Trim();
                continue;
            }

            var bssidMatch = BssidLineRegex.Match(trimmed);
            if (bssidMatch.Success)
            {
                if (ssid is null) continue;
                bssidFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["bssid"] = bssidMatch.Groups["bssid"].Value
                };
                bssidBlocks.Add(bssidFields);
                continue;
            }

            var keyValue = KeyValueRegex.Match(line);
            if (!keyValue.Success) continue;

            var key = NormalizeKey(keyValue.Groups["key"].Value);
            var value = keyValue.Groups["value"].Value.Trim();
            if (key.Length == 0 || value.Length == 0) continue;

            if (IsSsidLevelKey(key))
            {
                ssidFields[key] = value;
            }
            else if (bssidFields is not null)
            {
                bssidFields[key] = value;
            }
        }

        Flush();
        return result;
    }

    private static WifiNetworkInfo BuildNetwork(
        string ssid,
        Dictionary<string, string> fields,
        WifiNetworkInfo? current)
    {
        var bssid = PlatformText.NormalizeMac(GetValue(fields, "bssid"));
        var channel = PlatformText.ExtractFirstInteger(GetValue(fields, "channel", "通道", "信道"));
        var band = GetValue(fields, "band", "频带", "频段") ?? PlatformText.BandFromChannel(channel ?? 0);
        var security = BuildSecurity(
            GetValue(fields, "authentication", "身份验证"),
            GetValue(fields, "encryption", "cipher", "加密", "密码", "加密方式"));
        var rate = BuildRate(
            GetValue(fields, "receiverate(mbps)", "receiverate", "接收速率(mbps)", "接收速率"),
            GetValue(fields, "transmitrate(mbps)", "transmitrate", "传输速率(mbps)", "传输速率"));

        var isConnected = current is not null &&
                          ((bssid is not null && current.Bssid is not null &&
                            string.Equals(bssid, current.Bssid, StringComparison.OrdinalIgnoreCase)) ||
                           (current.Bssid is null && string.Equals(ssid, current.Ssid, StringComparison.OrdinalIgnoreCase)));

        return new WifiNetworkInfo(
            ssid,
            bssid,
            PlatformText.ParseSignalPercent(GetValue(fields, "signal", "信号")),
            channel,
            band,
            security,
            rate,
            isConnected);
    }

    private static List<Dictionary<string, string>> SplitInterfaceBlocks(string text)
    {
        var blocks = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            var match = KeyValueRegex.Match(line);
            if (!match.Success) continue;

            var key = NormalizeKey(match.Groups["key"].Value);
            var value = match.Groups["value"].Value.Trim();

            if (key is "name" or "名称")
            {
                if (current.Count > 0)
                {
                    blocks.Add(current);
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }

            if (!current.ContainsKey(key)) current[key] = value;
        }

        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    private static bool IsSsidLevelKey(string key) =>
        key is "networktype" or "网络类型" or "authentication" or "身份验证"
            or "encryption" or "加密" or "cipher" or "密码" or "加密方式";

    private static string? GetValue(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(NormalizeKey(key), out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeKey(string key) =>
        new string(key.Where(c => !char.IsWhiteSpace(c) && c != '.' && c != '\u3000').ToArray())
            .ToLowerInvariant();

    private static void MergeInto(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (var pair in source)
        {
            target.TryAdd(pair.Key, pair.Value);
        }
    }

    private static string? BuildSecurity(string? authentication, string? cipher)
    {
        if (string.IsNullOrWhiteSpace(authentication)) return string.IsNullOrWhiteSpace(cipher) ? null : cipher;
        if (string.IsNullOrWhiteSpace(cipher)) return authentication;
        return $"{authentication} / {cipher}";
    }

    private static string? BuildRate(string? receiveRate, string? transmitRate)
    {
        var receive = ToRate(receiveRate);
        var transmit = ToRate(transmitRate);
        if (receive is null && transmit is null) return null;
        if (receive is not null && transmit is not null) return $"{receive} / {transmit} Mbps";
        return $"{receive ?? transmit} Mbps";
    }

    private static string? ToRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Any(char.IsDigit) ? trimmed : null;
    }
}
