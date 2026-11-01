using System.Diagnostics;
using NetCable.Core.Models;

namespace NetCable.Core.Platform;

/// <summary>
/// 跨平台提权执行。提权进程无法重定向 stdout，因此调用方约定用 --out &lt;file&gt; 传递结果。
/// Windows: UAC (UseShellExecute + Verb=runas)；macOS: osascript 授权对话框；Linux: pkexec。
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                if (PlatformInfo.IsWindows) return ProcessRunner.IsElevatedAsync().GetAwaiter().GetResult();
                return Environment.UserName == "root" || ProcessRunner.IsElevatedAsync().GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份启动指定可执行文件并等待结束。
    /// 用户在 UAC/授权对话框点"否"时返回 CanceledByUser=true，不抛异常。
    /// </summary>
    public static async Task<ElevatedRunResult> RunElevatedAsync(
        string executablePath,
        IEnumerable<string> arguments,
        int timeoutMs = 60000,
        CancellationToken cancellationToken = default)
    {
        var exe = executablePath;
        var args = arguments.ToList();

        if (PlatformInfo.IsWindows)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            try
            {
                using var process = Process.Start(psi);
                if (process is null) return new ElevatedRunResult(false, false, false, -1, "无法启动提权进程");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeoutMs);
                try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(true); } catch { }
                    return new ElevatedRunResult(true, false, true, -1, "提权任务超时");
                }
                return new ElevatedRunResult(true, false, false, process.ExitCode, null);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 用户在 UAC 对话框中点了"否"
                return new ElevatedRunResult(false, true, false, -1, "用户在 UAC 授权对话框点了“否”");
            }
            catch (Exception ex)
            {
                return new ElevatedRunResult(false, false, false, -1, ex.Message);
            }
        }

        if (PlatformInfo.IsMacOS)
        {
            var quoted = string.Join(" ", args.Select(ShellQuote));
            var script = $"do shell script \"{exe.Replace("\"", "\\\"")} {quoted.Replace("\"", "\\\"")}\" with administrator privileges";
            return await RunLauncherAsync("osascript", new[] { "-e", script }, timeoutMs, cancellationToken).ConfigureAwait(false);
        }

        // Linux: pkexec 走 polkit 图形授权；不可用时提示用户用 sudo 启动
        if (ProcessRunner.ResolveCommand("pkexec") is null)
            return new ElevatedRunResult(false, false, false, -1, "未找到 pkexec（请安装 polkit，或以 sudo 启动 NetCable）");

        return await RunLauncherAsync("pkexec", new[] { exe }.Concat(args), timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ElevatedRunResult> RunLauncherAsync(
        string launcher, IEnumerable<string> args, int timeoutMs, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(launcher, args, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut) return new ElevatedRunResult(true, false, true, -1, "授权/执行超时");
        return new ElevatedRunResult(true, false, false, result.ExitCode,
            result.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(result.StandardError) ? result.CombinedOutput : result.StandardError));
    }

    private static string ShellQuote(string value) =>
        value.Any(c => c is ' ' or '"' or '\'' or '&' or ';' or '|')
            ? "'" + value.Replace("'", "'\\''") + "'"
            : value;
}
