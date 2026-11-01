using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// macOS 连接读取器：<c>netstat -an -p tcp/udp</c> 提供连接与状态，
/// <c>lsof -nP -i</c> 补全 PID 与进程名（netstat 本身不显示 PID）。
/// lsof 需要查询其他用户进程的权限，无权限时 PID 为 null。
/// </summary>
internal static class MacConnectionReader
{
    private static readonly Regex LsofLineRegex = new(
        @"^(?<command>\S+)\s+(?<pid>\d+)\s+.*?\s(?<proto>TCP|UDP)\s+(?<name>\S+)(?:\s+\((?<state>[^)]+)\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record LsofEntry(string Protocol, string Local, string Remote, string State, int Pid, string? ProcessName);

    /// <summary>读取 macOS 的 TCP/UDP 连接表。</summary>
    public static async Task<IReadOnlyList<ConnectionInfo>> ReadAsync(CancellationToken cancellationToken)
    {
        var tcpText = await CaptureAsync("netstat", new[] { "-an", "-p", "tcp" }, 12000, cancellationToken)
            .ConfigureAwait(false);
        var udpText = await CaptureAsync("netstat", new[] { "-an", "-p", "udp" }, 12000, cancellationToken)
            .ConfigureAwait(false);

        var connections = new List<ConnectionInfo>();
        connections.AddRange(ParseNetstat(tcpText, "TCP"));
        connections.AddRange(ParseNetstat(udpText, "UDP"));

        var lsofEntries = await ReadLsofAsync(cancellationToken).ConfigureAwait(false);
        if (lsofEntries.Count == 0) return connections;

        if (connections.Count == 0)
        {
            return lsofEntries
                .Select(e => new ConnectionInfo(e.Protocol, e.Local, e.Remote, e.State, e.Pid, e.ProcessName))
                .ToList();
        }

        // 用 (协议, 本地端口, 状态) 或 (协议, 本地端口) 匹配 lsof 结果补齐 PID
        var byPortAndState = new Dictionary<string, LsofEntry>(StringComparer.OrdinalIgnoreCase);
        var byPort = new Dictionary<string, LsofEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in lsofEntries)
        {
            var port = ConnectionReaders.ExtractPort(entry.Local);
            if (port is null) continue;
            byPortAndState.TryAdd($"{entry.Protocol}|{port.Value}|{entry.State}", entry);
            byPort.TryAdd($"{entry.Protocol}|{port.Value}", entry);
        }

        var result = new List<ConnectionInfo>(connections.Count);
        foreach (var connection in connections)
        {
            var port = ConnectionReaders.ExtractPort(connection.LocalEndPoint);
            if (port is null)
            {
                result.Add(connection);
                continue;
            }

            LsofEntry? entry = null;
            if (!byPortAndState.TryGetValue($"{connection.Protocol}|{port.Value}|{connection.State}", out var byState))
            {
                byPort.TryGetValue($"{connection.Protocol}|{port.Value}", out byState);
            }

            entry = byState;
            result.Add(entry is null
                ? connection
                : connection with { Pid = entry.Pid, ProcessName = entry.ProcessName });
        }

        return result;
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

    private static IReadOnlyList<ConnectionInfo> ParseNetstat(string? text, string protocol)
    {
        var result = new List<ConnectionInfo>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // tcp4  0  0  127.0.0.1.50000  127.0.0.1.50001  ESTABLISHED
            if (tokens.Length < 5) continue;

            var family = tokens[0].ToLowerInvariant();
            var isTcp = family.StartsWith("tcp", StringComparison.Ordinal);
            var isUdp = family.StartsWith("udp", StringComparison.Ordinal);
            if (!isTcp && !isUdp) continue;
            if (protocol == "TCP" && !isTcp) continue;
            if (protocol == "UDP" && !isUdp) continue;

            var local = ConvertEndpoint(tokens[3]);
            var remote = ConvertEndpoint(tokens[4]);
            string state;
            if (tokens.Length >= 6)
            {
                state = tokens[5].ToUpperInvariant();
            }
            else
            {
                state = ConnectionReaders.IsWildcardEndpoint(remote) ? "LISTEN" : "UNCONN";
            }

            result.Add(new ConnectionInfo(protocol, local, remote, state, null, null));
        }

        return result;
    }

    /// <summary>把 macOS netstat 的 <c>ip.port</c> 端点转换为 <c>ip:port</c> 形式。</summary>
    private static string ConvertEndpoint(string value)
    {
        if (value is "*.*" or "*:*") return "*:*";

        var lastDot = value.LastIndexOf('.');
        if (lastDot > 0 && lastDot < value.Length - 1)
        {
            return value[..lastDot] + ":" + value[(lastDot + 1)..];
        }

        return value;
    }

    private static async Task<List<LsofEntry>> ReadLsofAsync(CancellationToken cancellationToken)
    {
        var result = new List<LsofEntry>();
        var text = await CaptureAsync("lsof", new[] { "-nP", "-i" }, 20000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || !char.IsLetter(line[0])) continue;

            var match = LsofLineRegex.Match(line);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["pid"].Value, out var pid)) continue;

            var protocol = match.Groups["proto"].Value.ToUpperInvariant();
            var name = match.Groups["name"].Value;
            var state = match.Groups["state"].Success
                ? match.Groups["state"].Value.ToUpperInvariant()
                : string.Empty;

            var local = name;
            var remote = string.Empty;
            var arrow = name.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                local = name[..arrow];
                remote = name[(arrow + 2)..];
            }

            result.Add(new LsofEntry(protocol, local, remote, state, pid, match.Groups["command"].Value));
        }

        return result;
    }
}
