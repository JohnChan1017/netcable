using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetCable.Core.Platform;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
    public string CombinedOutput => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

public static class ProcessRunner
{
    public static string? ResolveCommand(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var extensions = new[] { ".exe", ".cmd", ".bat", "" };
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in pathDirs)
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        if (Path.IsPathRooted(name) && File.Exists(name)) return name;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        return psi;
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        int timeoutMs = 15000,
        CancellationToken cancellationToken = default)
    {
        if (ResolveCommand(fileName) is null)
            return new ProcessResult(-1, string.Empty, $"Command not found: {fileName}", false);

        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments) };
        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty, "Failed to start process", false);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
        }

        var stdout = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeReadAsync(stderrTask).ConfigureAwait(false);
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new ProcessResult(exitCode, stdout, stderr, timedOut);
    }

    public static async IAsyncEnumerable<string> StreamLinesAsync(
        string fileName,
        IEnumerable<string> arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments) };
        if (ResolveCommand(fileName) is null) yield break;
        try
        {
            if (!process.Start()) yield break;
        }
        catch
        {
            yield break;
        }

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    yield break;
                }
                if (line is null) break;
                yield return line;
            }
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); } catch { return string.Empty; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>当前进程是否以管理员/root 运行。</summary>
    public static Task<bool> IsElevatedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return Task.FromResult(principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        return IsRootAsync();
    }

    private static async Task<bool> IsRootAsync()
    {
        var result = await RunAsync("id", new[] { "-u" }, 3000).ConfigureAwait(false);
        return result.StandardOutput.Trim() == "0";
    }
}
