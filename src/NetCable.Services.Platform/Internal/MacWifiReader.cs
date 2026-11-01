using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// macOS WiFi 读取器：优先 <c>airport -I</c> / <c>airport -s</c>（macOS 14.4 起该工具被移除），
/// 不可用时回退 <c>system_profiler SPAirPortDataType -json</c>。
/// 解析失败或没有 WiFi 硬件时返回空结果，不抛异常。
/// </summary>
internal sealed class MacWifiReader
{
    private const string AirportBundlePath =
        "/System/Library/PrivateFrameworks/Apple80211.framework/Versions/Current/Resources/airport";

    private static readonly Regex KeyValueRegex = new(
        @"^\s*(?<key>[^:]+?)\s*:\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AirportScanRowRegex = new(
        @"^(?<ssid>.*?)\s+(?<bssid>(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2})\s+(?<rssi>-?\d+)\s+(?<channel>\S+)\s+(?<ht>\S+)\s+(?<cc>\S+)\s+(?<security>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FirstIntegerRegex = new(
        @"-?\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly bool _hasSystemProfiler = Commanding.Exists("system_profiler");

    /// <summary><c>airport</c> 可执行文件路径；在新版 macOS 上为 null。</summary>
    public static string? AirportPath { get; } = ResolveAirportPath();

    /// <summary>是否存在 system_profiler 命令。</summary>
    public bool HasSystemProfiler => _hasSystemProfiler;

    /// <summary>读取当前已连接的 WiFi（未连接返回 null）。</summary>
    public async Task<WifiNetworkInfo?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (AirportPath is not null)
        {
            var text = await CaptureAsync(AirportPath, new[] { "-I" }, 10000, cancellationToken)
                .ConfigureAwait(false);
            var current = ParseAirportCurrent(text);
            if (current is not null) return current;
        }

        if (!_hasSystemProfiler) return null;
        var snapshot = await ReadSystemProfilerAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Current;
    }

    /// <summary>扫描周边 WiFi。</summary>
    public async Task<IReadOnlyList<WifiNetworkInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        WifiNetworkInfo? current = null;
        if (AirportPath is not null)
        {
            var currentText = await CaptureAsync(AirportPath, new[] { "-I" }, 10000, cancellationToken)
                .ConfigureAwait(false);
            current = ParseAirportCurrent(currentText);

            var scanText = await CaptureAsync(AirportPath, new[] { "-s" }, 20000, cancellationToken)
                .ConfigureAwait(false);
            var scanned = ParseAirportScan(scanText, current);
            if (scanned.Count > 0) return scanned;
        }

        if (!_hasSystemProfiler) return Array.Empty<WifiNetworkInfo>();

        var snapshot = await ReadSystemProfilerAsync(cancellationToken).ConfigureAwait(false);
        var networks = snapshot.Networks.ToList();
        if (snapshot.Current is not null && networks.All(n => !n.IsConnected)) networks.Insert(0, snapshot.Current);
        return networks;
    }

    private static string? ResolveAirportPath()
    {
        try
        {
            if (Commanding.Exists("airport")) return "airport";
            return File.Exists(AirportBundlePath) ? AirportBundlePath : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> CaptureAsync(
        string command,
        string[] arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Commanding.CaptureAsync(command, arguments, timeoutMs, cancellationToken).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ airport

    private static WifiNetworkInfo? ParseAirportCurrent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var match = KeyValueRegex.Match(rawLine.TrimEnd('\r'));
            if (!match.Success) continue;

            var key = NormalizeKey(match.Groups["key"].Value);
            if (key.Length == 0) continue;
            fields.TryAdd(key, match.Groups["value"].Value.Trim());
        }

        var ssid = GetValue(fields, "ssid");
        if (string.IsNullOrWhiteSpace(ssid)) return null;

        var bssid = GetValue(fields, "bssid");
        var channelText = GetValue(fields, "channel");
        var channel = ParseFirstInteger(channelText);
        var frequency = ParseFrequency(channelText);
        var band = frequency is not null
            ? PlatformText.BandFromFrequency(frequency.Value)
            : PlatformText.BandFromChannel(channel ?? 0);

        var state = GetValue(fields, "state");
        var isConnected = state is null ||
                          state.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                          state.Contains("connected", StringComparison.OrdinalIgnoreCase);

        return new WifiNetworkInfo(
            ssid.Trim(),
            PlatformText.NormalizeMac(bssid),
            PlatformText.ParseSignalPercent(GetValue(fields, "agrctrlrssi", "agrctlrssi", "rssi")),
            channel,
            band,
            NormalizeSecurity(GetValue(fields, "security")),
            NormalizeRate(GetValue(fields, "lasttxrate")),
            isConnected);
    }

    private static IReadOnlyList<WifiNetworkInfo> ParseAirportScan(string? text, WifiNetworkInfo? current)
    {
        var result = new List<WifiNetworkInfo>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line.Contains("SSID BSSID", StringComparison.OrdinalIgnoreCase)) continue;

            var match = AirportScanRowRegex.Match(line);
            if (!match.Success) continue;

            var ssid = match.Groups["ssid"].Value.Trim();
            var bssid = PlatformText.NormalizeMac(match.Groups["bssid"].Value);
            if (ssid.Length == 0 || bssid is null) continue;
            if (!seen.Add(bssid)) continue;

            var channelText = match.Groups["channel"].Value;
            var channel = ParseFirstInteger(channelText);
            var isConnected = current is not null &&
                              ((current.Bssid is not null &&
                                string.Equals(bssid, current.Bssid, StringComparison.OrdinalIgnoreCase)) ||
                               (current.Bssid is null &&
                                string.Equals(ssid, current.Ssid, StringComparison.OrdinalIgnoreCase)));

            result.Add(new WifiNetworkInfo(
                ssid,
                bssid,
                PlatformText.ParseSignalPercent(match.Groups["rssi"].Value),
                channel,
                PlatformText.BandFromChannel(channel ?? 0),
                NormalizeSecurity(match.Groups["security"].Value.Trim()),
                null,
                isConnected));
        }

        return result;
    }

    // ------------------------------------------------------------------ system_profiler

    private sealed record SystemProfilerSnapshot(WifiNetworkInfo? Current, IReadOnlyList<WifiNetworkInfo> Networks);

    private async Task<SystemProfilerSnapshot> ReadSystemProfilerAsync(CancellationToken cancellationToken)
    {
        var text = await CaptureAsync("system_profiler", new[] { "SPAirPortDataType", "-json" }, 20000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new SystemProfilerSnapshot(null, Array.Empty<WifiNetworkInfo>());

        try
        {
            using var document = JsonDocument.Parse(text);
            var currentElement = FindFirstObject(document.RootElement, "spairport_current_network_information");
            var current = currentElement is null ? null : ParseSystemProfilerNetwork(currentElement.Value, true);

            var networks = new List<WifiNetworkInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var array in FindArrays(document.RootElement, "spairport_airport_other_local_wireless_networks"))
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var network = ParseSystemProfilerNetwork(item, false);
                    if (network is null) continue;
                    if (network.Bssid is not null && !seen.Add(network.Bssid)) continue;
                    networks.Add(network);
                }
            }

            return new SystemProfilerSnapshot(current, networks);
        }
        catch (JsonException)
        {
            return new SystemProfilerSnapshot(null, Array.Empty<WifiNetworkInfo>());
        }
        catch
        {
            return new SystemProfilerSnapshot(null, Array.Empty<WifiNetworkInfo>());
        }
    }

    private static WifiNetworkInfo? ParseSystemProfilerNetwork(JsonElement element, bool isConnected)
    {
        var ssid = GetJsonString(element, "_name") ?? GetJsonString(element, "spairport_network_name");
        if (string.IsNullOrWhiteSpace(ssid)) return null;

        var channelText = GetJsonString(element, "spairport_network_channel");
        var channel = ParseFirstInteger(channelText);
        var band = ParseBandFromText(channelText) ?? PlatformText.BandFromChannel(channel ?? 0);
        var signal = PlatformText.ParseSignalPercent(GetJsonString(element, "spairport_signal_noise"));
        var security = NormalizeSecurity(GetJsonString(element, "spairport_security_mode"));
        var rate = NormalizeRate(
            GetJsonString(element, "spairport_network_rate") ?? GetJsonString(element, "spairport_current_network_rate"));

        return new WifiNetworkInfo(
            ssid.Trim(),
            PlatformText.NormalizeMac(GetJsonString(element, "spairport_network_bssid")),
            signal,
            channel,
            band,
            security,
            rate,
            isConnected);
    }

    private static IEnumerable<JsonElement> FindArrays(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.Array)
                    {
                        yield return property.Value;
                    }

                    foreach (var child in FindArrays(property.Value, propertyName)) yield return child;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in FindArrays(item, propertyName)) yield return child;
                }

                break;
        }
    }

    private static JsonElement? FindFirstObject(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.Object)
                    {
                        return property.Value;
                    }

                    var nested = FindFirstObject(property.Value, propertyName);
                    if (nested is not null) return nested;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindFirstObject(item, propertyName);
                    if (nested is not null) return nested;
                }

                break;
        }

        return null;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    // ------------------------------------------------------------------ helpers

    private static string NormalizeKey(string key) =>
        new string(key.Where(c => !char.IsWhiteSpace(c) && c != '.' && c != '\u3000').ToArray())
            .ToLowerInvariant();

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

    private static int? ParseFirstInteger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = FirstIntegerRegex.Match(text);
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? ParseFrequency(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 形如 “2437”、“2437 MHz”、“6 (2.4GHz)”：优先取 MHz 数字，其次按信道折算
        var match = Regex.Match(text, @"(?<mhz>\d{3,4})\s*MHz", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (match.Success && long.TryParse(match.Groups["mhz"].Value, out var megahertz)) return megahertz;

        var bandText = ParseBandFromText(text);
        var channel = ParseFirstInteger(text);
        if (channel is null) return null;

        return bandText switch
        {
            "2.4 GHz" => 2407 + channel.Value * 5L,
            "5 GHz" => 5000 + channel.Value * 5L,
            "6 GHz" => 5950 + channel.Value * 5L,
            _ => null
        };
    }

    private static string? ParseBandFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("6GHz", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("6 GHz", StringComparison.OrdinalIgnoreCase)) return "6 GHz";
        if (text.Contains("5GHz", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("5 GHz", StringComparison.OrdinalIgnoreCase)) return "5 GHz";
        if (text.Contains("2.4GHz", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("2.4 GHz", StringComparison.OrdinalIgnoreCase)) return "2.4 GHz";
        return null;
    }

    private static string? NormalizeSecurity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("--", StringComparison.Ordinal) ||
            text.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return "Open";
        }

        const string prefix = "spairport_security_mode_";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }

        return text.Replace('_', ' ').ToUpperInvariant();
    }

    private static string? NormalizeRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Any(char.IsDigit) ? trimmed : null;
    }
}
