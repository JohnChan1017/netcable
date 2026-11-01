using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// macOS pf 防火墙只读读取器：<c>pfctl -s info</c> 取状态，<c>pfctl -sr</c> 取规则。
/// pfctl 通常需要 root；无权限时返回 Supported=false + 中文原因，规则返回空列表，不抛异常。
/// </summary>
internal static class MacFirewallReader
{
    private static readonly Regex PortRegex = new(
        @"port\s*=?\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>读取 pf 防火墙状态。</summary>
    public static async Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("pfctl", new[] { "-s", "info" }, 10000, cancellationToken).ConfigureAwait(false);
        if (result is null || !result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new FirewallStatus(
                false,
                "无法读取 pf 防火墙状态（需要 root 权限，可尝试 sudo pfctl -s info）",
                null,
                null,
                "pf (pfctl)");
        }

        bool? enabled = null;
        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase)) continue;

            if (line.Contains("Enabled", StringComparison.OrdinalIgnoreCase)) enabled = true;
            else if (line.Contains("Disabled", StringComparison.OrdinalIgnoreCase)) enabled = false;
            break;
        }

        return new FirewallStatus(true, null, enabled, "PF", "pfctl");
    }

    /// <summary>读取 pf 防火墙规则。</summary>
    public static async Task<IReadOnlyList<FirewallRule>> GetRulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("pfctl", new[] { "-sr" }, 10000, cancellationToken).ConfigureAwait(false);
        if (result is null || !result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<FirewallRule>();
        }

        return ParseRules(result.StandardOutput);
    }

    private static IReadOnlyList<FirewallRule> ParseRules(string text)
    {
        var result = new List<FirewallRule>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            var action = FirewallReaders.MapAction(tokens[0]);
            if (action.Length == 0) continue;

            string direction = string.Empty;
            for (var i = 1; i < tokens.Length; i++)
            {
                if (tokens[i] is "in" or "out") direction = tokens[i] == "in" ? "入站" : "出站";
            }

            string? protocol = null;
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (string.Equals(tokens[i], "proto", StringComparison.OrdinalIgnoreCase))
                {
                    protocol = tokens[i + 1].ToUpperInvariant();
                    break;
                }
            }

            string? localPort = null;
            var portMatch = PortRegex.Match(line);
            if (portMatch.Success) localPort = portMatch.Groups[1].Value;

            string? remoteAddress = null;
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (string.Equals(tokens[i], "from", StringComparison.OrdinalIgnoreCase))
                {
                    remoteAddress = FirewallReaders.NormalizeRemoteAddress(tokens[i + 1]);
                    break;
                }
            }

            result.Add(new FirewallRule(
                $"pf: {line}",
                direction.Length == 0 ? "未指定" : direction,
                action,
                protocol,
                localPort,
                remoteAddress,
                true));

            if (result.Count >= 2000) break;
        }

        return result;
    }

    private static async Task<ProcessResultSnapshot?> RunAsync(
        string command,
        string[] arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Commanding.RunAsync(command, arguments, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            return new ProcessResultSnapshot(result.Success, result.StandardOutput);
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

    private sealed record ProcessResultSnapshot(bool Success, string StandardOutput);
}
