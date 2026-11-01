using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Linux WiFi 读取器：优先用 NetworkManager 的 <c>nmcli</c>（无须 root），
/// 没有 nmcli 或读取失败时回退到 <c>iw dev</c> / <c>iw dev &lt;if&gt; link</c> / <c>iw dev &lt;if&gt; scan</c>
/// （iw scan 通常需要 root，无权限时优雅返回空列表）。
/// </summary>
internal sealed class LinuxWifiReader
{
    private const int TimeoutMs = 15000;

    private static readonly Regex IwInterfaceRegex = new(
        @"^\s*Interface\s+(?<name>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex IwBssRegex = new(
        @"^BSS\s+(?<bssid>[0-9a-fA-F:]{17})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly bool _hasNmcli;
    private readonly bool _hasIw;

    public LinuxWifiReader(bool hasNmcli, bool hasIw)
    {
        _hasNmcli = hasNmcli;
        _hasIw = hasIw;
    }

    /// <summary>读取当前已连接的 WiFi（未连接返回 null）。</summary>
    public async Task<WifiNetworkInfo?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (_hasNmcli)
        {
            var networks = await ReadNmcliAsync(cancellationToken).ConfigureAwait(false);
            var connected = networks.FirstOrDefault(n => n.IsConnected);
            if (connected is not null) return connected;
            if (networks.Count > 0) return null;
        }

        return _hasIw ? await ReadIwCurrentAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>扫描周边 WiFi。</summary>
    public async Task<IReadOnlyList<WifiNetworkInfo>> ScanAsync(CancellationToken cancellationToken)
    {
        if (_hasNmcli)
        {
            var viaNmcli = await ReadNmcliAsync(cancellationToken).ConfigureAwait(false);
            if (viaNmcli.Count > 0) return viaNmcli;
        }

        if (_hasIw)
        {
            var current = await ReadIwCurrentAsync(cancellationToken).ConfigureAwait(false);
            var scanned = await ReadIwScanAsync(current, cancellationToken).ConfigureAwait(false);
            if (scanned.Count > 0) return scanned;
            if (current is not null) return new[] { current };
        }

        return Array.Empty<WifiNetworkInfo>();
    }

    private async Task<string?> CaptureAsync(
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

    // ------------------------------------------------------------------ nmcli

    private async Task<List<WifiNetworkInfo>> ReadNmcliAsync(CancellationToken cancellationToken)
    {
        var primary = await CaptureAsync(
                "nmcli",
                new[] { "-t", "-f", "IN-USE,SSID,BSSID,SIGNAL,CHAN,FREQ,RATE,SECURITY", "dev", "wifi", "list" },
                TimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);

        var parsed = ParseNmcli(primary, withInUse: true);
        if (parsed.Count > 0) return parsed;

        // 老版本 nmcli 可能不支持 IN-USE/FREQ 字段，退回 6 列格式
        var fallback = await CaptureAsync(
                "nmcli",
                new[] { "-t", "-f", "SSID,BSSID,SIGNAL,CHAN,RATE,SECURITY", "dev", "wifi", "list" },
                TimeoutMs,
                cancellationToken)
            .ConfigureAwait(false);

        return ParseNmcli(fallback, withInUse: false);
    }

    private static List<WifiNetworkInfo> ParseNmcli(string? text, bool withInUse)
    {
        var result = new List<WifiNetworkInfo>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) continue;

            var fields = SplitTerse(line);
            string? ssid;
            string? bssid;
            string? signalText;
            string? channelText;
            string? frequencyText;
            string? rate;
            string? security;
            var isConnected = false;

            if (withInUse)
            {
                if (fields.Count < 8) continue;
                isConnected = fields[0].Trim() is "*" or "yes";
                ssid = fields[1];
                bssid = fields[2];
                signalText = fields[3];
                channelText = fields[4];
                frequencyText = fields[5];
                rate = fields[6];
                security = fields[7];
            }
            else
            {
                if (fields.Count < 6) continue;
                ssid = fields[0];
                bssid = fields[1];
                signalText = fields[2];
                channelText = fields[3];
                frequencyText = null;
                rate = fields[4];
                security = fields[5];
            }

            if (string.IsNullOrWhiteSpace(ssid)) continue;
            if (bssid is "--") bssid = null;

            var key = $"{ssid}\u0001{bssid}";
            if (!seen.Add(key)) continue;

            var channel = PlatformText.ExtractFirstInteger(channelText);
            var frequency = ParseFrequency(frequencyText);
            var band = frequency is not null
                ? PlatformText.BandFromFrequency(frequency.Value)
                : PlatformText.BandFromChannel(channel ?? 0);

            result.Add(new WifiNetworkInfo(
                ssid.Trim(),
                PlatformText.NormalizeMac(bssid),
                ParseInt(signalText),
                channel,
                band,
                string.IsNullOrWhiteSpace(security) ? null : security.Trim(),
                string.IsNullOrWhiteSpace(rate) ? null : rate.Trim(),
                isConnected));
        }

        return result;
    }

    /// <summary>按 nmcli terse 模式拆分字段：<c>\:</c> 是转义的冒号。</summary>
    private static List<string> SplitTerse(string line)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '\\' && i + 1 < line.Length)
            {
                builder.Append(line[i + 1]);
                i++;
                continue;
            }

            if (character == ':')
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        fields.Add(builder.ToString());
        return fields;
    }

    // ------------------------------------------------------------------ iw

    private async Task<string?> FindWirelessInterfaceAsync(CancellationToken cancellationToken)
    {
        var text = await CaptureAsync("iw", new[] { "dev" }, 8000, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = IwInterfaceRegex.Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private async Task<WifiNetworkInfo?> ReadIwCurrentAsync(CancellationToken cancellationToken)
    {
        var interfaceName = await FindWirelessInterfaceAsync(cancellationToken).ConfigureAwait(false);
        if (interfaceName is null) return null;

        var text = await CaptureAsync("iw", new[] { "dev", interfaceName, "link" }, 10000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("Not connected", StringComparison.OrdinalIgnoreCase)) return null;

        var bssid = Regex.Match(text, @"Connected to\s+(?<mac>[0-9a-fA-F:]{17})", RegexOptions.CultureInvariant);
        var ssid = MatchValue(text, "SSID:");
        if (string.IsNullOrWhiteSpace(ssid)) return null;

        var frequency = ParseFrequency(MatchValue(text, "freq:"));
        var signalText = MatchValue(text, "signal:");
        var receiveRate = MatchValue(text, "rx bitrate:");
        var transmitRate = MatchValue(text, "tx bitrate:");
        var channel = frequency is null ? null : PlatformText.ChannelFromFrequency(frequency.Value);
        var band = frequency is null ? PlatformText.BandFromChannel(channel ?? 0) : PlatformText.BandFromFrequency(frequency.Value);

        return new WifiNetworkInfo(
            ssid.Trim(),
            bssid.Success ? PlatformText.NormalizeMac(bssid.Groups["mac"].Value) : null,
            PlatformText.ParseSignalPercent(signalText),
            channel,
            band,
            null,
            BuildRate(transmitRate, receiveRate),
            true);
    }

    private async Task<IReadOnlyList<WifiNetworkInfo>> ReadIwScanAsync(
        WifiNetworkInfo? current,
        CancellationToken cancellationToken)
    {
        var interfaceName = await FindWirelessInterfaceAsync(cancellationToken).ConfigureAwait(false);
        if (interfaceName is null) return Array.Empty<WifiNetworkInfo>();

        var text = await CaptureAsync("iw", new[] { "dev", interfaceName, "scan" }, 20000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<WifiNetworkInfo>();

        var result = new List<WifiNetworkInfo>();
        string? blockBssid = null;
        string? blockSsid = null;
        long? blockFrequency = null;
        string? blockSignal = null;
        string? blockRate = null;
        var hasRsn = false;
        var hasWpa = false;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(blockSsid) || string.IsNullOrWhiteSpace(blockBssid))
            {
                hasRsn = false;
                hasWpa = false;
                return;
            }

            var channel = blockFrequency is null ? null : PlatformText.ChannelFromFrequency(blockFrequency.Value);
            var band = blockFrequency is null
                ? PlatformText.BandFromChannel(channel ?? 0)
                : PlatformText.BandFromFrequency(blockFrequency.Value);
            var security = hasRsn ? "WPA2/WPA3 (RSN)" : hasWpa ? "WPA" : "Open";
            var isConnected = current is not null &&
                              string.Equals(blockBssid, current.Bssid, StringComparison.OrdinalIgnoreCase);

            result.Add(new WifiNetworkInfo(
                blockSsid.Trim(),
                PlatformText.NormalizeMac(blockBssid),
                PlatformText.ParseSignalPercent(blockSignal),
                channel,
                band,
                security,
                blockRate,
                isConnected));

            hasRsn = false;
            hasWpa = false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var bssMatch = IwBssRegex.Match(line);
            if (bssMatch.Success)
            {
                Flush();
                blockBssid = bssMatch.Groups["bssid"].Value;
                blockSsid = null;
                blockFrequency = null;
                blockSignal = null;
                blockRate = null;
                continue;
            }

            if (blockBssid is null) continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("SSID:", StringComparison.Ordinal))
            {
                blockSsid = trimmed["SSID:".Length..].Trim();
            }
            else if (trimmed.StartsWith("freq:", StringComparison.Ordinal))
            {
                blockFrequency = ParseFrequency(trimmed["freq:".Length..].Trim());
            }
            else if (trimmed.StartsWith("signal:", StringComparison.Ordinal))
            {
                blockSignal = trimmed["signal:".Length..].Trim();
            }
            else if (trimmed.StartsWith("tx bitrate:", StringComparison.Ordinal))
            {
                blockRate = trimmed["tx bitrate:".Length..].Trim();
            }
            else if (trimmed.StartsWith("RSN:", StringComparison.Ordinal))
            {
                hasRsn = true;
            }
            else if (trimmed.StartsWith("WPA:", StringComparison.Ordinal))
            {
                hasWpa = true;
            }
        }

        Flush();
        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static string? MatchValue(string text, string prefix)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static long? ParseFrequency(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var number = Regex.Match(text, @"\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
        if (!number.Success) return null;
        return double.TryParse(number.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (long)value
            : null;
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? BuildRate(string? transmitRate, string? receiveRate)
    {
        if (string.IsNullOrWhiteSpace(transmitRate) && string.IsNullOrWhiteSpace(receiveRate)) return null;
        if (!string.IsNullOrWhiteSpace(transmitRate) && !string.IsNullOrWhiteSpace(receiveRate))
        {
            return $"Rx {receiveRate} / Tx {transmitRate}";
        }

        return (transmitRate ?? receiveRate)!.Trim();
    }
}
