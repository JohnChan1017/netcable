using System.Text.Json;
using System.Text.Json.Serialization;
using NetCable.Core.Models;
using NetCable.Core.Platform;
using NetCable.Services.Network;
using NetCable.Services.Platform;

namespace NetCable.Desktop.Services;

/// <summary>
/// 提权任务桥：GUI 以管理员身份重新拉起自身（同一个 exe）执行 headless 任务，
/// 任务把 JSON 写入临时文件，GUI 读取后展示。提权进程无法重定向 stdout，所以必须走文件。
/// </summary>
public static class ElevatedBridge
{
    public const string TaskFlag = "--elevated-task";
    private const string OutFlag = "--out";
    private const string SecondsFlag = "--seconds";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsElevatedTask(string[] args) => args.Contains(TaskFlag, StringComparer.OrdinalIgnoreCase);

    /// <summary>由 Program.Main 在提权实例里调用；返回进程退出码。</summary>
    public static int RunElevatedTask(string[] args)
    {
        var task = GetArg(args, TaskFlag) ?? string.Empty;
        var outPath = GetArg(args, OutFlag);
        if (string.IsNullOrWhiteSpace(outPath))
            return 2;

        try
        {
            switch (task)
            {
                case "wifi-bss":
                {
                    var service = new WifiBssService();
                    // 先在提权实例里再确认一次权限：服务层遇到 ACCESS_DENIED 会返回空列表，
                    // 这里必须把"拒绝访问"如实报给 GUI，避免把"没有 AP"和"没权限"混为一谈。
                    var access = service.GetAccessStateAsync().GetAwaiter().GetResult();
                    if (!access.CanReadBssList)
                    {
                        WriteJson(outPath, new WifiBssPayload(false, access.BlockReason ?? "无法读取 WLAN AP 列表", Array.Empty<WifiBssDetail>()));
                        return 5;
                    }

                    var items = service.GetBssListAsync().GetAwaiter().GetResult();
                    WriteJson(outPath, new WifiBssPayload(true, null, items));
                    return 0;
                }
                case "wifi-monitor":
                {
                    var seconds = int.TryParse(GetArg(args, SecondsFlag), out var s) ? Math.Clamp(s, 3, 120) : 15;
                    var service = new WifiMonitorService();
                    var capability = service.GetCapabilityAsync().GetAwaiter().GetResult();
                    if (!capability.Supported)
                    {
                        WriteJson(outPath, new WifiMonitorPayload(false, capability.Reason ?? "monitor mode 不可用", Array.Empty<WifiFrameInfo>()));
                        return 3;
                    }

                    var frames = new List<WifiFrameInfo>();
                    var enumerator = service.CaptureAsync(seconds, CancellationToken.None).GetAsyncEnumerator();
                    try
                    {
                        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                        {
                            frames.Add(enumerator.Current);
                            if (frames.Count >= 5000) break;
                        }
                    }
                    finally
                    {
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }

                    WriteJson(outPath, new WifiMonitorPayload(true, null, frames));
                    return 0;
                }
                default:
                    WriteJson(outPath, new ErrorPayload(false, "未知提权任务：" + task));
                    return 2;
            }
        }
        catch (Exception ex)
        {
            WriteJson(outPath, new ErrorPayload(false, ex.Message));
            return 1;
        }
    }

    /// <summary>弹窗提权执行 WLAN AP 详情查询；返回 null 表示用户取消或失败（error 里有原因）。</summary>
    public static async Task<(IReadOnlyList<WifiBssDetail>? Items, string? Error)> RunWifiBssAsync(CancellationToken cancellationToken = default)
    {
        var (ok, payload, error) = await RunTaskAsync<WifiBssPayload>("wifi-bss", null, cancellationToken).ConfigureAwait(false);
        return ok && payload is { Ok: true } ? (payload.Items, null) : (null, error ?? payload?.Error ?? "未返回结果");
    }

    /// <summary>弹窗提权执行 802.11 管理帧捕获。</summary>
    public static async Task<(IReadOnlyList<WifiFrameInfo>? Frames, string? Error)> RunWifiMonitorAsync(int seconds, CancellationToken cancellationToken = default)
    {
        var (ok, payload, error) = await RunTaskAsync<WifiMonitorPayload>("wifi-monitor", seconds, cancellationToken).ConfigureAwait(false);
        return ok && payload is { Ok: true } ? (payload.Frames, null) : (null, error ?? payload?.Error ?? "未返回结果");
    }

    private static async Task<(bool Ok, T? Payload, string? Error)> RunTaskAsync<T>(
        string task, int? seconds, CancellationToken cancellationToken) where T : class
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return (false, null, "无法定位当前可执行文件路径");

        var outFile = Path.Combine(Path.GetTempPath(), $"netcable-{task}-{Guid.NewGuid():N}.json");
        var args = new List<string> { TaskFlag, task, OutFlag, outFile };
        if (seconds is not null) args.AddRange(new[] { SecondsFlag, seconds.Value.ToString() });

        try
        {
            var result = await Elevation.RunElevatedAsync(exe, args, timeoutMs: seconds is null ? 60000 : (seconds.Value + 30) * 1000, cancellationToken).ConfigureAwait(false);
            if (!result.Started)
                return (false, null, result.CanceledByUser ? "已取消授权（未获得管理员权限）" : (result.Error ?? "提权失败"));

            if (!File.Exists(outFile))
                return (false, null, result.Error ?? $"提权任务未产生结果文件（退出码 {result.ExitCode}）");

            var json = await File.ReadAllTextAsync(outFile, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return (true, payload, result.ExitCode == 0 ? null : payload is WifiMonitorPayload { Error: { } e } ? e : null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        }
    }

    private static void WriteJson<T>(string path, T payload)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions)); } catch { }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return args.FirstOrDefault(a => a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))?[(flag.Length + 1)..];
    }

    public sealed record WifiBssPayload(bool Ok, string? Error, IReadOnlyList<WifiBssDetail> Items);
    public sealed record WifiMonitorPayload(bool Ok, string? Error, IReadOnlyList<WifiFrameInfo> Frames);
    public sealed record ErrorPayload(bool Ok, string? Error);
}
