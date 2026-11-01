using NetCable.Core.Models;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// Windows 连接读取器，解析 <c>netstat -ano</c>。
/// 中文/英文系统的状态列（LISTENING/ESTABLISHED…）与协议列（TCP/UDP）都是英文，
/// 因此不需要本地化关键字；行首两列固定为协议与本地端点。
/// </summary>
internal static class WindowsConnectionReader
{
    /// <summary>读取 Windows 的 TCP/UDP 连接表。</summary>
    public static async Task<IReadOnlyList<ConnectionInfo>> ReadAsync(CancellationToken cancellationToken)
    {
        string? text;
        try
        {
            text = await Commanding.CaptureAsync("netstat", new[] { "-ano" }, 20000, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<ConnectionInfo>();
        }

        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ConnectionInfo>();

        var result = new List<ConnectionInfo>();
        var processNames = new Dictionary<int, string?>();

        foreach (var rawLine in text.Split('\n'))
        {
            var tokens = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;

            var protocol = tokens[0].ToUpperInvariant();
            if (protocol == "TCP")
            {
                // TCP  local  remote  state  pid
                if (tokens.Length < 5) continue;
                if (!int.TryParse(tokens[4], out var tcpPid)) continue;
                result.Add(new ConnectionInfo(
                    "TCP",
                    tokens[1],
                    tokens[2],
                    tokens[3].ToUpperInvariant(),
                    tcpPid,
                    ConnectionReaders.ResolveProcessName(tcpPid, processNames)));
            }
            else if (protocol == "UDP")
            {
                // UDP  local  remote  pid
                if (!int.TryParse(tokens[3], out var udpPid)) continue;
                var remote = tokens[2];
                var state = ConnectionReaders.IsWildcardEndpoint(remote) ? "LISTEN" : "UNCONN";
                result.Add(new ConnectionInfo(
                    "UDP",
                    tokens[1],
                    remote,
                    state,
                    udpPid,
                    ConnectionReaders.ResolveProcessName(udpPid, processNames)));
            }
        }

        await FillMissingProcessNamesAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 某些受限/降权环境里 <see cref="System.Diagnostics.Process.GetProcessById"/> 无法枚举进程，
    /// 此时退回 <c>tasklist</c> 一次性建立 PID → 进程名映射。
    /// </summary>
    private static async Task FillMissingProcessNamesAsync(
        List<ConnectionInfo> connections,
        CancellationToken cancellationToken)
    {
        if (connections.All(c => !string.IsNullOrWhiteSpace(c.ProcessName))) return;

        string? text;
        try
        {
            text = await Commanding.CaptureAsync("tasklist", new[] { "-fo", "csv", "-nh" }, 10000, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        var names = new Dictionary<int, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('"')) continue;

            // "chrome.exe","1234","Console","1","100,000 K"
            var parts = line.Split("\",\"", StringSplitOptions.None);
            if (parts.Length < 2) continue;
            var imageName = parts[0].TrimStart('"');
            if (!int.TryParse(parts[1].Trim('"'), out var pid)) continue;
            names[pid] = Path.GetFileNameWithoutExtension(imageName);
        }

        if (names.Count == 0) return;

        for (var i = 0; i < connections.Count; i++)
        {
            var connection = connections[i];
            if (connection.Pid is > 0 &&
                string.IsNullOrWhiteSpace(connection.ProcessName) &&
                names.TryGetValue(connection.Pid.Value, out var name))
            {
                connections[i] = connection with { ProcessName = name };
            }
        }
    }
}
