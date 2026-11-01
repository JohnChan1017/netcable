using System.Text;
using NetCable.Core.Platform;

namespace NetCable.Services.Platform.Internal;

/// <summary>
/// 系统命令执行的统一封装。
/// Windows 上先用 <c>chcp 65001</c> 切到 UTF-8 代码页再执行，避免中文系统输出（GBK）
/// 被 <see cref="ProcessRunner"/> 以 UTF-8 读取后成为乱码，导致中英文关键字解析失败；
/// macOS/Linux 直接执行。命令不存在、无权限、超时都返回 null / 空结果，从不抛异常。
/// </summary>
internal static class Commanding
{
    /// <summary>判断命令是否存在于 PATH 中。</summary>
    public static bool Exists(string command)
    {
        try
        {
            return ProcessRunner.ResolveCommand(command) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>执行命令并返回完整输出（stdout + stderr）；失败或超时返回 null。</summary>
    public static async Task<string?> CaptureAsync(
        string command,
        string[] arguments,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync(command, arguments, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut) return null;
            var text = result.CombinedOutput;
            return string.IsNullOrWhiteSpace(text) ? null : text;
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

    /// <summary>执行命令并返回原始结果（不吞掉 stderr），用于需要区分退出码的场景。</summary>
    public static Task<ProcessResult> RunAsync(
        string command,
        string[] arguments,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        if (PlatformInfo.IsWindows)
        {
            // cmd /c 会把子命令的退出码透传出来，同时 chcp 让子命令输出 UTF-8。
            var commandLine = "chcp 65001>nul & " + BuildWindowsCommandLine(command, arguments);
            return ProcessRunner.RunAsync("cmd", new[] { "/c", commandLine }, Math.Max(timeoutMs, 1), cancellationToken);
        }

        return ProcessRunner.RunAsync(command, arguments, Math.Max(timeoutMs, 1), cancellationToken);
    }

    private static string BuildWindowsCommandLine(string command, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return command;

        var builder = new StringBuilder(command);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            var needsQuotes = argument.Length == 0
                              || argument.Any(char.IsWhiteSpace)
                              || argument.IndexOfAny(new[] { '&', '|', '<', '>', '^', '"' }) >= 0;
            if (!needsQuotes)
            {
                builder.Append(argument);
                continue;
            }

            builder.Append('"');
            builder.Append(argument.Replace("\"", "\\\"", StringComparison.Ordinal));
            builder.Append('"');
        }

        return builder.ToString();
    }
}
