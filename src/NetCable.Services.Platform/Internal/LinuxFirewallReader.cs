using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Linux 防火墙只读读取器：优先 <c>nft list ruleset</c>，回退 <c>iptables -S</c>。
/// 无 root/CAP_NET_ADMIN 权限或命令缺失时返回 Supported=false + 中文原因；
/// 规则逐条解析，无法识别的行直接跳过。
/// </summary>
internal static class LinuxFirewallReader
{
    private static readonly Regex DportRegex = new(
        @"dport\s+(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SaddrRegex = new(
        @"(?:saddr|from)\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "tcp", "udp", "udplite", "sctp", "icmp", "icmpv6", "ip", "ip6", "ether", "arp"
    };

    /// <summary>读取 Linux 防火墙状态。</summary>
    public static async Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nft = await RunAsync("nft", new[] { "list", "ruleset" }, 15000, cancellationToken).ConfigureAwait(false);
        if (nft is { Success: true })
        {
            var text = nft.StandardOutput;
            var enabled = text.Contains("hook input", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("hook output", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("hook forward", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("policy drop", StringComparison.OrdinalIgnoreCase);
            return new FirewallStatus(true, null, enabled, "nftables", "nft");
        }

        var iptables = await RunAsync("iptables", new[] { "-S" }, 15000, cancellationToken).ConfigureAwait(false);
        if (iptables is { Success: true })
        {
            var hasRules = iptables.StandardOutput
                .Split('\n')
                .Any(line => line.TrimStart().StartsWith("-A ", StringComparison.Ordinal));
            return new FirewallStatus(true, null, hasRules, "iptables", "iptables");
        }

        return new FirewallStatus(
            false,
            "无法读取防火墙：需要 root/CAP_NET_ADMIN 权限，或未安装 nft / iptables",
            null,
            null,
            null);
    }

    /// <summary>读取 Linux 防火墙规则。</summary>
    public static async Task<IReadOnlyList<FirewallRule>> GetRulesAsync(CancellationToken cancellationToken)
    {
        var nft = await RunAsync("nft", new[] { "list", "ruleset" }, 15000, cancellationToken).ConfigureAwait(false);
        if (nft is { Success: true } && !string.IsNullOrWhiteSpace(nft.StandardOutput))
        {
            var parsed = ParseNftRuleset(nft.StandardOutput);
            if (parsed.Count > 0) return parsed;
        }

        var iptables = await RunAsync("iptables", new[] { "-S" }, 15000, cancellationToken).ConfigureAwait(false);
        if (iptables is { Success: true } && !string.IsNullOrWhiteSpace(iptables.StandardOutput))
        {
            return ParseIptables(iptables.StandardOutput);
        }

        return Array.Empty<FirewallRule>();
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

    // ------------------------------------------------------------------ nft

    private static IReadOnlyList<FirewallRule> ParseNftRuleset(string text)
    {
        var result = new List<FirewallRule>();
        string? table = null;
        string? chain = null;
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (trimmed.StartsWith("table ", StringComparison.Ordinal))
            {
                var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                table = tokens.Length >= 3 ? $"{tokens[1]} {tokens[2]}" : null;
                chain = null;
                continue;
            }

            if (trimmed.StartsWith("chain ", StringComparison.Ordinal))
            {
                var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                chain = tokens.Length >= 2 ? tokens[1] : null;
                if (chain is not null) counters[chain] = 0;
                continue;
            }

            if (trimmed == "}")
            {
                chain = null;
                continue;
            }

            if (chain is null) continue;
            if (trimmed.StartsWith("type ", StringComparison.Ordinal) ||
                trimmed.StartsWith("policy ", StringComparison.Ordinal))
            {
                continue;
            }

            counters.TryGetValue(chain, out var number);
            counters[chain] = number + 1;

            var tokensOfRule = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokensOfRule.Length == 0) continue;

            var action = FirewallReaders.MapAction(tokensOfRule[^1]);
            if (action.Length == 0) continue;

            var protocol = tokensOfRule.Length > 0 && KnownProtocols.Contains(tokensOfRule[0])
                ? tokensOfRule[0].ToUpperInvariant()
                : null;

            var localPort = DportRegex.Match(trimmed) is { Success: true } portMatch
                ? portMatch.Groups[1].Value
                : null;
            var remoteAddress = SaddrRegex.Match(trimmed) is { Success: true } addressMatch
                ? FirewallReaders.NormalizeRemoteAddress(addressMatch.Groups[1].Value)
                : null;

            result.Add(new FirewallRule(
                $"{table}/{chain}: {trimmed}",
                FirewallReaders.DirectionFromChain(chain),
                action,
                protocol,
                localPort,
                remoteAddress,
                true));

            if (result.Count >= 2000) break;
        }

        return result;
    }

    // ------------------------------------------------------------------ iptables

    private static IReadOnlyList<FirewallRule> ParseIptables(string text)
    {
        var result = new List<FirewallRule>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("-A ", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            var chain = tokens[1];
            var action = FirewallReaders.MapAction(ValueAfter(tokens, "-j"));
            if (action.Length == 0) continue;

            var protocol = ValueAfter(tokens, "-p")?.ToUpperInvariant();
            var localPort = FirewallReaders.NormalizePort(ValueAfter(tokens, "--dport") ?? ValueAfter(tokens, "--dports"));
            var remoteAddress = FirewallReaders.NormalizeRemoteAddress(ValueAfter(tokens, "-s"));

            result.Add(new FirewallRule(
                line,
                FirewallReaders.DirectionFromChain(chain),
                action,
                protocol,
                localPort,
                remoteAddress,
                true));

            if (result.Count >= 2000) break;
        }

        return result;
    }

    private static string? ValueAfter(string[] tokens, string key)
    {
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (string.Equals(tokens[i], key, StringComparison.OrdinalIgnoreCase)) return tokens[i + 1];
        }

        return null;
    }
}
