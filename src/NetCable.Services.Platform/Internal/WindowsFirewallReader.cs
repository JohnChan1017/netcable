using System.Globalization;
using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Windows 防火墙只读读取器。
/// 状态优先 <c>netsh advfirewall show allprofiles</c>（需管理员），
/// 非管理员会话回退读取注册表 <c>EnableFirewall</c>（普通用户可读）；
/// 规则优先 <c>netsh advfirewall firewall show rule name=all verbose</c>，
/// 失败时回退解析注册表 <c>FirewallRules</c> 缓存。所有输出中英文兼容，解析失败跳过。
/// </summary>
internal static class WindowsFirewallReader
{
    private const string FirewallPolicyKey =
        @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    private const string FirewallRulesKey =
        @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    private static readonly Regex KeyValueRegex = new(
        @"^(?<key>[^:]+?)\s*:\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RegistryValueRegex = new(
        @"^\s+(?<name>\S+)\s+REG_SZ\s+(?<data>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RegistryDwordRegex = new(
        @"EnableFirewall\s+REG_DWORD\s+(?<value>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>读取 Windows Defender 防火墙状态。</summary>
    public static async Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // 1) netsh：管理员会话下最权威
        var netshOutput = await RunCaptureAsync(
            "netsh", new[] { "advfirewall", "show", "allprofiles" }, 15000, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(netshOutput))
        {
            var profiles = ParseNetshProfiles(netshOutput);
            if (profiles.Count > 0)
            {
                return new FirewallStatus(
                    true,
                    null,
                    profiles.Values.All(enabled => enabled),
                    FormatProfiles(profiles),
                    "Windows Defender 防火墙 (netsh advfirewall)");
            }
        }

        // 2) 注册表回退：非管理员会话可读开关状态
        var registryOutput = await RunCaptureAsync(
                "reg", new[] { "query", FirewallPolicyKey, "/s", "/v", "EnableFirewall" }, 12000, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(registryOutput))
        {
            var profiles = ParseRegistryProfiles(registryOutput);
            if (profiles.Count > 0)
            {
                return new FirewallStatus(
                    true,
                    "当前会话非管理员：实时状态与规则需管理员权限，已从注册表读取防火墙开关",
                    profiles.Values.All(enabled => enabled),
                    FormatProfiles(profiles),
                    "Windows Defender 防火墙 (注册表回退)");
            }
        }

        return new FirewallStatus(
            false,
            "无法读取防火墙状态：需要管理员权限，且注册表回退失败",
            null,
            null,
            "Windows Defender 防火墙");
    }

    /// <summary>读取 Windows 防火墙规则。</summary>
    public static async Task<IReadOnlyList<FirewallRule>> GetRulesAsync(CancellationToken cancellationToken)
    {
        // 1) netsh 详细规则（需管理员）
        var output = await RunCaptureAsync(
                "netsh",
                new[] { "advfirewall", "firewall", "show", "rule", "name=all", "verbose" },
                45000,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(output))
        {
            var parsed = ParseNetshRules(output);
            if (parsed.Count > 0) return parsed;
        }

        // 2) 注册表缓存规则（非管理员可读）
        var registryOutput = await RunCaptureAsync(
                "reg", new[] { "query", FirewallRulesKey }, 30000, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(registryOutput)
            ? Array.Empty<FirewallRule>()
            : ParseRegistryRules(registryOutput);
    }

    // ------------------------------------------------------------------ 状态解析

    private static Dictionary<string, bool> ParseNetshProfiles(string text)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string? currentProfile = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            if ((line.Contains("Profile", StringComparison.OrdinalIgnoreCase) &&
                 line.Contains("Settings", StringComparison.OrdinalIgnoreCase)) ||
                line.Contains("配置文件", StringComparison.Ordinal))
            {
                currentProfile = MapProfileName(line);
                continue;
            }

            if (currentProfile is null) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            var key = tokens[0].TrimEnd(':');
            if (key is not ("State" or "状态")) continue;

            var value = tokens[^1];
            var enabled = ParseEnabledText(value);
            if (enabled is not null) result[currentProfile] = enabled.Value;

            currentProfile = null;
        }

        return result;
    }

    private static Dictionary<string, bool> ParseRegistryProfiles(string text)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string? currentProfile = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                currentProfile = MapRegistryProfileName(line);
                continue;
            }

            if (currentProfile is null) continue;
            if (!line.StartsWith("EnableFirewall", StringComparison.OrdinalIgnoreCase)) continue;

            var value = ParseRegistryDword(line);
            if (value is not null) result[currentProfile] = value.Value;
        }

        return result;
    }

    private static string? MapProfileName(string line)
    {
        if (line.Contains("Domain", StringComparison.OrdinalIgnoreCase) || line.Contains("域", StringComparison.Ordinal))
            return "域";
        if (line.Contains("Private", StringComparison.OrdinalIgnoreCase) || line.Contains("专用", StringComparison.Ordinal))
            return "专用";
        if (line.Contains("Public", StringComparison.OrdinalIgnoreCase) || line.Contains("公用", StringComparison.Ordinal))
            return "公用";
        return null;
    }

    private static string? MapRegistryProfileName(string line)
    {
        if (line.Contains("DomainProfile", StringComparison.OrdinalIgnoreCase)) return "域";
        if (line.Contains("StandardProfile", StringComparison.OrdinalIgnoreCase)) return "专用";
        if (line.Contains("PublicProfile", StringComparison.OrdinalIgnoreCase)) return "公用";
        return null;
    }

    private static bool? ParseEnabledText(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;

        if (text.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
            text is "是" or "启用" or "1")
        {
            return true;
        }

        if (text.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("NO", StringComparison.OrdinalIgnoreCase) ||
            text is "否" or "禁用" or "0")
        {
            return false;
        }

        return null;
    }

    private static bool? ParseRegistryDword(string line)
    {
        var match = RegistryDwordRegex.Match(line);
        if (!match.Success) return null;

        var value = match.Groups["value"].Value;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex != 0;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number != 0;
    }

    private static string FormatProfiles(Dictionary<string, bool> profiles) =>
        string.Join(" · ", profiles.Select(pair => $"{pair.Key}: {(pair.Value ? "启用" : "禁用")}"));

    // ------------------------------------------------------------------ 规则解析

    private static IReadOnlyList<FirewallRule> ParseNetshRules(string text)
    {
        var result = new List<FirewallRule>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastKey = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            // 分隔线 / 结尾提示
            if (trimmed.All(c => c is '-' or '=' or ' ')) continue;
            if (trimmed.Equals("OK.", StringComparison.OrdinalIgnoreCase)) continue;

            var match = KeyValueRegex.Match(trimmed);
            if (!match.Success)
            {
                // 值换行续接
                if (lastKey is not null && current.ContainsKey(lastKey))
                {
                    current[lastKey] = (current[lastKey] + " " + trimmed).Trim();
                }

                continue;
            }

            var key = NormalizeKey(match.Groups["key"].Value);
            var value = match.Groups["value"].Value.Trim();

            if (key is "rulename" or "规则名称")
            {
                FlushNetshRule(result, current);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                lastKey = key;
                if (value.Length > 0) current[key] = value;
                continue;
            }

            if (key.Length == 0) continue;
            current[key] = value;
            lastKey = key;
        }

        FlushNetshRule(result, current);
        return result;
    }

    private static void FlushNetshRule(List<FirewallRule> result, Dictionary<string, string> fields)
    {
        if (fields.Count == 0) return;

        var name = GetField(fields, "rulename", "规则名称");
        if (string.IsNullOrWhiteSpace(name)) return;

        var direction = FirewallReaders.MapDirection(GetField(fields, "direction", "方向"));
        var action = FirewallReaders.MapAction(GetField(fields, "action", "操作"));
        if (direction.Length == 0 || action.Length == 0) return;

        var enabled = ParseEnabledText(GetField(fields, "enabled", "已启用") ?? "YES") ?? true;
        var protocol = NormalizeNullable(GetField(fields, "protocol", "协议"));
        var localPort = FirewallReaders.NormalizePort(GetField(fields, "localport", "本地端口"));
        var remoteAddress = FirewallReaders.NormalizeRemoteAddress(GetField(fields, "remoteip", "远程ip", "远程IP"));

        result.Add(new FirewallRule(name!, direction, action, protocol, localPort, remoteAddress, enabled));
    }

    private static IReadOnlyList<FirewallRule> ParseRegistryRules(string text)
    {
        var result = new List<FirewallRule>();

        foreach (var rawLine in text.Split('\n'))
        {
            var match = RegistryValueRegex.Match(rawLine.TrimEnd('\r'));
            if (!match.Success) continue;

            var valueName = match.Groups["name"].Value.Trim();
            var data = match.Groups["data"].Value.Trim();
            if (data.Length == 0) continue;

            var fields = ParsePipeFields(data);
            var action = FirewallReaders.MapAction(fields.GetValueOrDefault("Action"));
            var direction = FirewallReaders.MapDirection(fields.GetValueOrDefault("Dir"));
            if (action.Length == 0 || direction.Length == 0) continue;

            var name = fields.GetValueOrDefault("Name");
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@')) name = valueName;

            var protocol = MapRegistryProtocol(fields.GetValueOrDefault("Protocol"));
            var localPort = FirewallReaders.NormalizePort(
                fields.GetValueOrDefault("LPort") ?? fields.GetValueOrDefault("RPort"));
            var remoteAddress = FirewallReaders.NormalizeRemoteAddress(
                fields.GetValueOrDefault("RA4") ?? fields.GetValueOrDefault("RA6"));

            var enabled = !string.Equals(
                fields.GetValueOrDefault("Active"), "FALSE", StringComparison.OrdinalIgnoreCase);

            result.Add(new FirewallRule(name!, direction, action, protocol, localPort, remoteAddress, enabled));
            if (result.Count >= 5000) break;
        }

        return result;
    }

    private static Dictionary<string, string> ParsePipeFields(string data)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in data.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;

            var key = part[..separator].Trim();
            if (key.Length == 0 || fields.ContainsKey(key)) continue;
            fields[key] = part[(separator + 1)..];
        }

        return fields;
    }

    private static string? MapRegistryProtocol(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim() switch
        {
            "6" => "TCP",
            "17" => "UDP",
            "1" => "ICMPv4",
            "58" => "ICMPv6",
            "256" => null,
            "0" => null,
            var other => other
        };
    }

    private static string? GetField(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeKey(string key) =>
        new string(key.Where(c => !char.IsWhiteSpace(c) && c != '.' && c != '\u3000' && c != ':').ToArray())
            .ToLowerInvariant();

    /// <summary>执行命令：仅在退出码为 0 时返回输出（失败/无权限/超时返回 null）。</summary>
    private static async Task<string?> RunCaptureAsync(
        string command,
        string[] arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Commanding.RunAsync(command, arguments, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
            return result.StandardOutput;
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
}
