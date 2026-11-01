using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetCable.Core.Platform;
namespace NetCable.Services.Platform.Internal;

/// <summary>远程客户端类型。</summary>
internal enum RemoteClientKind
{
    Rdp,
    Ssh,
    Vnc,
    PowerShell
}

/// <summary>一个远程客户端的能力与可执行文件信息。</summary>
internal sealed record RemoteClient(
    RemoteClientKind Kind,
    string Id,
    string Title,
    string Protocol,
    bool IsAvailable,
    string? Executable,
    string? ExecutablePath,
    string? Hint);

/// <summary>解析后的连接目标（host、可选端口、可选用户名）。</summary>
internal sealed record RemoteTarget(string Host, int? Port, string? User);

/// <summary>
/// 远程客户端目录：三平台的原生客户端探测、命令行拼装与进程拉起。
/// 所有目标输入都会经过严格校验后再拼装参数，不经过 shell 注入路径。
/// </summary>
internal static class RemoteClients
{
    private static readonly Regex HostNameRegex = new(
        @"^[A-Za-z0-9_](?:[A-Za-z0-9._-]*[A-Za-z0-9_])?\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserNameRegex = new(
        @"^[A-Za-z0-9._-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PortRegex = new(
        @"^\d{1,5}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Linux 下可用的终端模拟器（命令名，终端参数前缀）。</summary>
    private static readonly (string Command, string Prefix)[] LinuxTerminals =
    {
        ("x-terminal-emulator", "-e"),
        ("gnome-terminal", "--"),
        ("konsole", "-e"),
        ("xfce4-terminal", "-x"),
        ("xterm", "-e")
    };

    /// <summary>探测当前平台可用的远程客户端（固定返回 4 项，未安装的标记为不可用并给出中文提示）。</summary>
    public static IReadOnlyList<RemoteClient> Detect() => PlatformInfo.Family switch
    {
        OsFamily.Windows => DetectWindows(),
        OsFamily.MacOS => DetectMacOS(),
        OsFamily.Linux => DetectLinux(),
        _ => DetectUnknown()
    };

    // ------------------------------------------------------------------ 探测

    private static IReadOnlyList<RemoteClient> DetectWindows()
    {
        var mstsc = FindCommand("mstsc");
        var ssh = FindCommand("ssh");
        var vnc = FindCommand("vncviewer", "tvnviewer", "uvncviewer", "vncviewer64");
        var pwsh = FindCommand("pwsh");
        var powershell = pwsh ?? FindCommand("powershell") ?? FindWindowsPowerShell();
        var powershellName = powershell is null ? null : Path.GetFileNameWithoutExtension(powershell);

        return new[]
        {
            new RemoteClient(
                RemoteClientKind.Rdp, "rdp", "远程桌面", "RDP",
                mstsc is not null,
                mstsc is null ? null : "mstsc",
                mstsc,
                mstsc is null ? "未找到 mstsc（Windows 远程桌面连接）" : "使用 Windows 远程桌面连接 (mstsc)"),
            new RemoteClient(
                RemoteClientKind.Ssh, "ssh", "SSH", "SSH",
                ssh is not null,
                ssh is null ? null : "ssh",
                ssh,
                ssh is null ? "未安装 OpenSSH 客户端（设置 → 应用 → 可选功能 → OpenSSH 客户端）" : "使用系统 OpenSSH 客户端"),
            new RemoteClient(
                RemoteClientKind.Vnc, "vnc", "VNC", "VNC",
                vnc is not null,
                vnc is null ? null : Path.GetFileNameWithoutExtension(vnc),
                vnc,
                vnc is null ? "未安装 VNC 客户端（可安装 TigerVNC / RealVNC / UltraVNC）" : "使用系统 VNC 客户端"),
            new RemoteClient(
                RemoteClientKind.PowerShell, "powershell", "PowerShell", "LOCAL",
                powershell is not null,
                powershellName,
                powershell,
                powershell is null ? "未找到 PowerShell（powershell.exe 或 pwsh）" : "打开本机 PowerShell 终端")
        };
    }

    private static IReadOnlyList<RemoteClient> DetectMacOS()
    {
        var open = FindCommand("open") ?? FindFile("/usr/bin/open");
        var ssh = FindCommand("ssh");
        var pwsh = FindCommand("pwsh");

        var rdpApp = FindApplication(
            "Microsoft Remote Desktop.app",
            "Windows App.app",
            "Microsoft Remote Desktop Beta.app") ?? FindApplicationUnder("/Applications",
            "Microsoft Remote Desktop.app",
            "Windows App.app");

        var screenSharing = FindApplicationUnder(
                                "/System/Applications/Utilities",
                                "Screen Sharing.app")
                            ?? FindApplicationUnder("/System/Library/CoreServices/Applications", "Screen Sharing.app")
                            ?? FindApplication("Screen Sharing.app");

        return new[]
        {
            new RemoteClient(
                RemoteClientKind.Rdp, "rdp", "远程桌面", "RDP",
                rdpApp is not null || open is not null,
                rdpApp ?? open,
                open,
                rdpApp is not null
                    ? "使用已安装的 Microsoft Remote Desktop"
                    : "未检测到 Microsoft Remote Desktop；将尝试用 open 唤起 rdp:// 链接"),
            new RemoteClient(
                RemoteClientKind.Ssh, "ssh", "SSH", "SSH",
                ssh is not null,
                ssh is null ? null : "ssh",
                ssh,
                ssh is null ? "未安装 OpenSSH 客户端（macOS 自带 /usr/bin/ssh）" : "使用系统 OpenSSH 客户端"),
            new RemoteClient(
                RemoteClientKind.Vnc, "vnc", "VNC", "VNC",
                screenSharing is not null || open is not null,
                screenSharing ?? open,
                open,
                screenSharing is not null
                    ? "使用系统“屏幕共享”连接 vnc://"
                    : "未找到“屏幕共享”，将尝试用 open 唤起 vnc:// 链接"),
            new RemoteClient(
                RemoteClientKind.PowerShell, "powershell", "PowerShell", "LOCAL",
                pwsh is not null,
                pwsh is null ? null : "pwsh",
                pwsh,
                pwsh is null ? "未安装 PowerShell 7（pwsh）" : "打开本机 PowerShell 终端")
        };
    }

    private static IReadOnlyList<RemoteClient> DetectLinux()
    {
        var rdp = FindCommand("xfreerdp", "xfreerdp3", "freerdp", "remmina");
        var vnc = FindCommand("vncviewer", "xtigervncviewer", "tigervnc", "remmina");
        var ssh = FindCommand("ssh");
        var pwsh = FindCommand("pwsh");

        return new[]
        {
            new RemoteClient(
                RemoteClientKind.Rdp, "rdp", "远程桌面", "RDP",
                rdp is not null,
                rdp is null ? null : Path.GetFileName(rdp),
                rdp,
                rdp is null ? "未安装 FreeRDP（Debian/Ubuntu: apt install freerdp2-x11；Fedora: dnf install freerdp）" : "使用 FreeRDP"),
            new RemoteClient(
                RemoteClientKind.Ssh, "ssh", "SSH", "SSH",
                ssh is not null,
                ssh is null ? null : "ssh",
                ssh,
                ssh is null ? "未安装 OpenSSH 客户端（apt install openssh-client）" : "使用系统 OpenSSH 客户端"),
            new RemoteClient(
                RemoteClientKind.Vnc, "vnc", "VNC", "VNC",
                vnc is not null,
                vnc is null ? null : Path.GetFileName(vnc),
                vnc,
                vnc is null ? "未安装 VNC 客户端（apt install tigervnc-viewer）" : "使用系统 VNC 客户端"),
            new RemoteClient(
                RemoteClientKind.PowerShell, "powershell", "PowerShell", "LOCAL",
                pwsh is not null,
                pwsh is null ? null : "pwsh",
                pwsh,
                pwsh is null ? "未安装 PowerShell 7（pwsh）" : "打开本机 PowerShell 终端")
        };
    }

    private static IReadOnlyList<RemoteClient> DetectUnknown()
    {
        var hint = "当前平台不支持远程客户端启动";
        return new[]
        {
            new RemoteClient(RemoteClientKind.Rdp, "rdp", "远程桌面", "RDP", false, null, null, hint),
            new RemoteClient(RemoteClientKind.Ssh, "ssh", "SSH", "SSH", false, null, null, hint),
            new RemoteClient(RemoteClientKind.Vnc, "vnc", "VNC", "VNC", false, null, null, hint),
            new RemoteClient(RemoteClientKind.PowerShell, "powershell", "PowerShell", "LOCAL", false, null, null, hint)
        };
    }

    // ------------------------------------------------------------------ 目标解析

    /// <summary>解析目标字符串：host、host:port、user@host、user@host:port，支持 IPv6 方括号写法。</summary>
    public static bool TryParseTarget(string? target, out RemoteTarget? result, out string? error)
    {
        result = null;
        error = null;

        var text = (target ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "请输入目标主机，例如 example.com、example.com:3389 或 user@example.com";
            return false;
        }

        // 去掉协议前缀与路径（rdp://user@host:3389/path）
        var schemeIndex = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0) text = text[(schemeIndex + 3)..];

        var slashIndex = text.IndexOf('/');
        if (slashIndex > 0) text = text[..slashIndex];

        string? user = null;
        var atIndex = text.LastIndexOf('@');
        if (atIndex >= 0)
        {
            user = text[..atIndex].Trim();
            text = text[(atIndex + 1)..].Trim();
            if (user.Length == 0)
            {
                error = "用户名不能为空（user@host）";
                return false;
            }

            if (!UserNameRegex.IsMatch(user))
            {
                error = $"用户名格式无效：{user}";
                return false;
            }
        }

        if (text.Length == 0)
        {
            error = "缺少主机名";
            return false;
        }

        string host;
        int? port = null;

        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0)
            {
                error = "IPv6 地址缺少右方括号";
                return false;
            }

            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryParsePort(rest[1..], out port))
                {
                    error = "端口格式无效";
                    return false;
                }
            }

            if (!System.Net.IPAddress.TryParse(host, out var ipv6) ||
                ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                error = $"IPv6 地址无效：{host}";
                return false;
            }
        }
        else
        {
            var colonIndex = text.LastIndexOf(':');
            if (colonIndex > 0 && text.IndexOf(':') == colonIndex)
            {
                var portText = text[(colonIndex + 1)..];
                if (portText.Length > 0 && PortRegex.IsMatch(portText))
                {
                    if (!TryParsePort(portText, out port))
                    {
                        error = "端口需在 1-65535 之间";
                        return false;
                    }

                    text = text[..colonIndex];
                }
            }

            host = text;
            if (!System.Net.IPAddress.TryParse(host, out _) && !HostNameRegex.IsMatch(host))
            {
                error = $"目标格式无效：{host}";
                return false;
            }
        }

        if (host.Length == 0)
        {
            error = "缺少主机名";
            return false;
        }

        result = new RemoteTarget(host, port, user);
        return true;
    }

    private static bool TryParsePort(string text, out int? port)
    {
        port = null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;
        if (value is < 1 or > 65535) return false;
        port = value;
        return true;
    }

    // ------------------------------------------------------------------ 命令行拼装

    /// <summary>返回可直接展示/复制的命令行（不执行）。</summary>
    public static string BuildCommand(RemoteClient client, RemoteTarget? target)
    {
        var commandName = CommandName(client);

        if (client.Kind == RemoteClientKind.PowerShell)
        {
            return commandName;
        }

        if (target is null) return commandName;

        return PlatformInfo.Family switch
        {
            OsFamily.Windows => BuildWindowsCommand(client, target, commandName),
            OsFamily.MacOS => BuildMacCommand(client, target, commandName),
            OsFamily.Linux => BuildLinuxCommand(client, target, commandName),
            _ => commandName
        };
    }

    private static string BuildWindowsCommand(RemoteClient client, RemoteTarget target, string commandName) =>
        client.Kind switch
        {
            RemoteClientKind.Rdp => $"mstsc /v:{HostPort(target)}",
            RemoteClientKind.Ssh => BuildSshCommand(target),
            RemoteClientKind.Vnc => $"{commandName} {VncTarget(target)}",
            _ => commandName
        };

    private static string BuildMacCommand(RemoteClient client, RemoteTarget target, string commandName) =>
        client.Kind switch
        {
            RemoteClientKind.Rdp => $"open \"rdp://{UrlAuthority(target)}\"",
            RemoteClientKind.Ssh => BuildSshCommand(target),
            RemoteClientKind.Vnc => $"open \"vnc://{HostPort(target)}\"",
            _ => commandName
        };

    private static string BuildLinuxCommand(RemoteClient client, RemoteTarget target, string commandName) =>
        client.Kind switch
        {
            RemoteClientKind.Rdp => commandName == "remmina"
                ? $"remmina -c rdp://{UrlAuthority(target)}"
                : $"{commandName} /v:{HostPort(target)}{(target.User is null ? string.Empty : $" /u:{target.User}")}",
            RemoteClientKind.Ssh => BuildSshCommand(target),
            RemoteClientKind.Vnc => commandName == "remmina"
                ? $"remmina -c vnc://{HostPort(target)}"
                : $"{commandName} {VncTarget(target)}",
            _ => commandName
        };

    private static string BuildSshCommand(RemoteTarget target)
    {
        var sshTarget = target.User is null ? target.Host : $"{target.User}@{target.Host}";
        return target.Port is { } port ? $"ssh {sshTarget} -p {port}" : $"ssh {sshTarget}";
    }

    /// <summary>VNC 客户端使用 <c>host::port</c> 约定。</summary>
    private static string VncTarget(RemoteTarget target) =>
        target.Port is { } port ? $"{FormatHost(target.Host)}::{port}" : FormatHost(target.Host);

    /// <summary>用于 URL 的 authority：可选 user@、IPv6 方括号、可选端口。</summary>
    private static string UrlAuthority(RemoteTarget target)
    {
        var userPart = target.User is null ? string.Empty : $"{target.User}@";
        return $"{userPart}{HostPort(target)}";
    }

    private static string HostPort(RemoteTarget target) =>
        target.Port is { } port ? $"{FormatHost(target.Host)}:{port}" : FormatHost(target.Host);

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static string CommandName(RemoteClient client)
    {
        if (!string.IsNullOrWhiteSpace(client.Executable)) return client.Executable!;
        if (!string.IsNullOrWhiteSpace(client.ExecutablePath))
            return Path.GetFileNameWithoutExtension(client.ExecutablePath);
        return client.Kind switch
        {
            RemoteClientKind.Rdp => "mstsc",
            RemoteClientKind.Ssh => "ssh",
            RemoteClientKind.Vnc => "vncviewer",
            _ => "pwsh"
        };
    }

    // ------------------------------------------------------------------ 启动

    /// <summary>启动系统原生客户端并返回说明文本（失败时返回中文原因而不是抛异常）。</summary>
    public static string Launch(RemoteClient client, RemoteTarget? target)
    {
        try
        {
            return PlatformInfo.Family switch
            {
                OsFamily.Windows => LaunchWindows(client, target),
                OsFamily.MacOS => LaunchMacOS(client, target),
                OsFamily.Linux => LaunchLinux(client, target),
                _ => "当前平台不支持启动远程客户端"
            };
        }
        catch (Exception ex)
        {
            return $"启动失败：{ex.Message}";
        }
    }

    private static string LaunchWindows(RemoteClient client, RemoteTarget? target)
    {
        var executable = client.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return $"未找到 {client.Title} 可执行文件：{client.Hint}";
        }

        var arguments = BuildLaunchArguments(client, target);
        var commandLine = QuoteWindows(executable) +
                          (arguments.Count == 0 ? string.Empty : " " + string.Join(' ', arguments.Select(QuoteWindows)));

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"start \"\" {commandLine}");

        using var process = Process.Start(startInfo);
        return $"已启动 {client.Title}：{BuildCommand(client, target)}";
    }

    private static string LaunchMacOS(RemoteClient client, RemoteTarget? target)
    {
        var open = FindCommand("open") ?? FindFile("/usr/bin/open") ?? "open";

        switch (client.Kind)
        {
            case RemoteClientKind.Rdp:
            {
                if (target is null) return "请输入远程桌面目标（host 或 user@host）";
                var url = $"rdp://{UrlAuthority(target)}";
                using var process = Process.Start(CreateStartInfo(open, new[] { url }));
                return $"已通过 open 唤起远程桌面：{url}";
            }

            case RemoteClientKind.Vnc:
            {
                if (target is null) return "请输入 VNC 目标（host:port）";
                var url = $"vnc://{HostPort(target)}";
                using var process = Process.Start(CreateStartInfo(open, new[] { url }));
                return $"已通过屏幕共享连接：{url}";
            }

            default:
                return LaunchMacTerminal(client, target);
        }
    }

    private static string LaunchMacTerminal(RemoteClient client, RemoteTarget? target)
    {
        var executable = client.ExecutablePath ?? CommandName(client);
        var arguments = BuildLaunchArguments(client, target);
        var commandText = BuildShellCommand(executable, arguments);

        var script = $"tell application \"Terminal\" to do script \"{EscapeAppleScript(commandText)}\"";
        var activate = "tell application \"Terminal\" to activate";
        var osascript = FindCommand("osascript") ?? "/usr/bin/osascript";

        using var process = Process.Start(CreateStartInfo(osascript, new[] { "-e", script, "-e", activate }));
        return $"已在 Terminal 中启动：{BuildCommand(client, target)}";
    }

    private static string LaunchLinux(RemoteClient client, RemoteTarget? target)
    {
        var executable = client.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return $"未找到 {client.Title} 可执行文件：{client.Hint}";
        }

        if (client.Kind is RemoteClientKind.Rdp or RemoteClientKind.Vnc)
        {
            using var process = Process.Start(CreateStartInfo(executable, BuildLaunchArguments(client, target).ToArray()));
            return $"已启动 {client.Title}：{BuildCommand(client, target)}";
        }

        // SSH / PowerShell：需要终端模拟器
        foreach (var (command, prefix) in LinuxTerminals)
        {
            var terminal = FindCommand(command);
            if (terminal is null) continue;

            var arguments = new List<string> { prefix };
            arguments.Add(executable);
            arguments.AddRange(BuildLaunchArguments(client, target));

            using var process = Process.Start(CreateStartInfo(terminal, arguments.ToArray()));
            return $"已在 {command} 中启动：{BuildCommand(client, target)}";
        }

        return $"未找到可用终端模拟器，请手动执行：{BuildCommand(client, target)}";
    }

    private static List<string> BuildLaunchArguments(RemoteClient client, RemoteTarget? target)
    {
        var arguments = new List<string>();
        if (client.Kind == RemoteClientKind.PowerShell)
        {
            arguments.Add("-NoExit");
            return arguments;
        }

        if (target is null) return arguments;

        switch (client.Kind)
        {
            case RemoteClientKind.Rdp:
                arguments.Add($"/v:{HostPort(target)}");
                if (PlatformInfo.IsLinux && target.User is not null) arguments.Add($"/u:{target.User}");
                break;

            case RemoteClientKind.Vnc:
                arguments.Add(VncTarget(target));
                break;

            case RemoteClientKind.Ssh:
                arguments.Add(target.User is null ? target.Host : $"{target.User}@{target.Host}");
                if (target.Port is { } sshPort)
                {
                    arguments.Add("-p");
                    arguments.Add(sshPort.ToString(CultureInfo.InvariantCulture));
                }

                break;
        }

        return arguments;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string BuildShellCommand(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteShell(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(QuoteShell(argument));
        }

        return builder.ToString();
    }

    private static string QuoteShell(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':' or '@' or '+' or '=' or ','))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string QuoteWindows(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '&' && c != '|' && c != '<' && c != '>' && c != '"' && c != '^'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    // ------------------------------------------------------------------ 文件探测

    private static string? FindCommand(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var resolved = ProcessRunner.ResolveCommand(name);
                if (resolved is not null) return resolved;
            }
            catch
            {
                // 忽略单个命令探测失败
            }
        }

        return null;
    }

    private static string? FindFile(string path)
    {
        try
        {
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindApplication(params string[] applicationNames)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in applicationNames)
        {
            string[] candidates =
            {
                Path.Combine("/Applications", name),
                Path.Combine(home, "Applications", name)
            };

            foreach (var candidate in candidates)
            {
                if (SafeDirectoryExists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string? FindApplicationUnder(string directory, params string[] applicationNames)
    {
        foreach (var name in applicationNames)
        {
            var candidate = Path.Combine(directory, name);
            if (SafeDirectoryExists(candidate)) return candidate;
        }

        return null;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindWindowsPowerShell()
    {
        try
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrWhiteSpace(systemRoot)) return null;
            var path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
