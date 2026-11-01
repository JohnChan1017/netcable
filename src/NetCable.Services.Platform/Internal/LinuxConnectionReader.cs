using System.Text.RegularExpressions;
using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Linux 连接读取器：优先 <c>ss -tunap</c>，无 ss 时回退 <c>netstat -tunap</c>。
/// 无 root 权限时进程名可能缺失，此时 PID/进程名为 null，其余字段仍然有效。
/// </summary>
internal static class LinuxConnectionReader
{
    private static readonly Regex SsPidRegex = new(@"pid=(?<pid>\d+)", RegexOptions.Compiled);
    private static readonly Regex SsProcessRegex = new(@"\(""(?<name>[^""]+)""", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownNetstatStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "LISTEN", "ESTABLISHED", "ESTAB", "TIME_WAIT", "CLOSE_WAIT", "SYN_SENT", "SYN_RECV",
        "FIN_WAIT1", "FIN_WAIT2", "LAST_ACK", "CLOSING", "UNCONN", "CLOSE", "DELETE"
    };

    /// <summary>读取 Linux 的 TCP/UDP 连接表。</summary>
    public static async Task<IReadOnlyList<ConnectionInfo>> ReadAsync(CancellationToken cancellationToken)
    {
        var ssText = await CaptureAsync("ss", new[] { "-tunap" }, 15000, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ssText))
        {
            var parsed = ParseSs(ssText);
            if (parsed.Count > 0) return parsed;
        }

        var netstatText = await CaptureAsync("netstat", new[] { "-tunap" }, 15000, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(netstatText)
            ? Array.Empty<ConnectionInfo>()
            : ParseNetstat(netstatText);
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

    private static IReadOnlyList<ConnectionInfo> ParseSs(string text)
    {
        var result = new List<ConnectionInfo>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Netid State Recv-Q Send-Q Local Peer [process]
            if (tokens.Length < 6) continue;

            var protocol = tokens[0].ToUpperInvariant();
            if (protocol is not ("TCP" or "UDP")) continue;

            var state = NormalizeState(protocol, tokens[1]);
            var local = tokens[4];
            var remote = tokens[5];

            var rest = tokens.Length > 6 ? string.Join(' ', tokens[6..]) : string.Empty;
            var pidMatch = SsPidRegex.Match(rest);
            var processMatch = SsProcessRegex.Match(rest);
            int? pid = pidMatch.Success && int.TryParse(pidMatch.Groups["pid"].Value, out var parsedPid)
                ? parsedPid
                : null;
            var processName = processMatch.Success ? processMatch.Groups["name"].Value : null;

            result.Add(new ConnectionInfo(protocol, local, remote, state, pid, processName));
        }

        return result;
    }

    private static IReadOnlyList<ConnectionInfo> ParseNetstat(string text)
    {
        var result = new List<ConnectionInfo>();
        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Proto Recv-Q Send-Q Local Foreign [State] [PID/Program]
            if (tokens.Length < 5) continue;

            var protocol = tokens[0].ToUpperInvariant();
            if (protocol is not ("TCP" or "UDP")) continue;

            var local = tokens[3];
            var remote = tokens[4];

            var state = string.Empty;
            var index = 5;
            if (index < tokens.Length && KnownNetstatStates.Contains(tokens[index]))
            {
                state = tokens[index].ToUpperInvariant();
                index++;
            }

            int? pid = null;
            string? processName = null;
            if (index < tokens.Length)
            {
                var program = tokens[index];
                if (program != "-")
                {
                    var slash = program.IndexOf('/');
                    if (slash > 0 && int.TryParse(program[..slash], out var parsedPid))
                    {
                        pid = parsedPid;
                        processName = program[(slash + 1)..];
                    }
                    else
                    {
                        processName = program;
                    }
                }
            }

            if (protocol == "UDP" && state.Length == 0)
            {
                state = ConnectionReaders.IsWildcardEndpoint(remote) ? "LISTEN" : "UNCONN";
            }

            result.Add(new ConnectionInfo(protocol, local, remote, state, pid, processName));
        }

        return result;
    }

    private static string NormalizeState(string protocol, string state)
    {
        var value = state.ToUpperInvariant();
        if (value == "UNCONN") return protocol == "UDP" ? "LISTEN" : value;
        if (value == "ESTAB") return "ESTABLISHED";
        return value.Replace('-', '_');
    }
}
