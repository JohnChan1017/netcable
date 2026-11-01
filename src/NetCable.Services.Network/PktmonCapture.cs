using NetCable.Core.Platform;

namespace NetCable.Services.Network;

/// <summary>
/// Windows pktmon 后备后端：Windows 10 1809+ 内置、无需安装第三方组件，但需要管理员权限。
/// 流程：<c>pktmon start --capture --pkt-size 200 -f xxx.etl</c> → 等待 N 秒 → <c>pktmon stop</c>
/// → <c>pktmon etl2pcap xxx.etl -o xxx.pcap</c>，生成的 pcap 交给既有解析器处理。
/// </summary>
internal static class PktmonCapture
{
    public const string BackendName = "Windows pktmon";

    /// <summary>探测 pktmon 是否存在（仅 Windows）。</summary>
    public static bool IsAvailable(out string? toolPath)
    {
        toolPath = null;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            toolPath = ProcessRunner.ResolveCommand("pktmon");
        }
        catch
        {
            toolPath = null;
        }
        return toolPath is not null;
    }

    /// <summary>
    /// 抓包并转换为 pcap 文件。成功返回可解析的临时 pcap 路径（调用方负责删除）；
    /// 失败返回 null 与中文原因。用户取消时抛出 OperationCanceledException。
    /// </summary>
    public static async Task<(string? PcapPath, string? Error)> CaptureToPcapAsync(
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable(out var toolPath) || toolPath is null)
            return (null, "pktmon 不可用（仅 Windows 10 1809+ 提供）。");

        var etlPath = Path.Combine(Path.GetTempPath(), $"netcable-pktmon-{Guid.NewGuid():N}.etl");
        var pcapPath = Path.Combine(Path.GetTempPath(), $"netcable-pktmon-{Guid.NewGuid():N}.pcap");
        var sessionStarted = false;

        try
        {
            var startArguments = new[]
            {
                "start", "--capture", "--pkt-size", "200", "-f", etlPath
            };
            var start = await ProcessRunner.RunAsync(toolPath, startArguments, 30000, cancellationToken)
                .ConfigureAwait(false);
            if (!start.Success)
            {
                return (null, BuildError("pktmon start 失败（通常需要管理员权限）", start));
            }
            sessionStarted = true;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1, 60)), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // 无论正常结束还是取消，都要停止会话，避免残留 pktmon 会话
                sessionStarted = false;
                await StopSessionAsync(toolPath).ConfigureAwait(false);
            }

            if (!File.Exists(etlPath) || new FileInfo(etlPath).Length == 0)
                return (null, "pktmon 未生成 ETL 抓包文件（可能抓包窗口内没有流量）。");

            var convert = await ProcessRunner.RunAsync(
                    toolPath, new[] { "etl2pcap", etlPath, "-o", pcapPath }, 60000, cancellationToken)
                .ConfigureAwait(false);
            if (!convert.Success || !File.Exists(pcapPath) || new FileInfo(pcapPath).Length < 24)
            {
                TryDelete(pcapPath);
                return (null, BuildError("pktmon etl2pcap 转换失败", convert));
            }

            return (pcapPath, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sessionStarted) await StopSessionAsync(toolPath).ConfigureAwait(false);
            TryDelete(pcapPath);
            throw;
        }
        catch (Exception ex)
        {
            if (sessionStarted) await StopSessionAsync(toolPath).ConfigureAwait(false);
            TryDelete(pcapPath);
            return (null, $"Windows pktmon 抓包失败：{ex.Message}");
        }
        finally
        {
            TryDelete(etlPath);
        }
    }

    private static async Task StopSessionAsync(string toolPath)
    {
        try
        {
            await ProcessRunner.RunAsync(toolPath, new[] { "stop" }, 20000, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // 停止失败不应影响结果返回
        }
    }

    private static string BuildError(string message, ProcessResult result)
    {
        var detail = result.StandardError.Trim();
        if (detail.Length == 0) detail = result.StandardOutput.Trim();
        if (detail.Length == 0) return message;
        if (detail.Length > 400) detail = detail[..400] + "…";
        return $"{message}：{detail}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件删除失败忽略
        }
    }
}
