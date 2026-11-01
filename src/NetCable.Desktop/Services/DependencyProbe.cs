using System.Diagnostics;
using NetCable.Core.Platform;

namespace NetCable.Desktop.Services;

public sealed record DependencyStatus(
    string Id,
    string Name,
    bool Installed,
    string? Version,
    string? Path,
    string Purpose,
    string? Hint,
    string? DownloadUrl = null,
    bool BundledInstallerAvailable = false);

/// <summary>
/// 抓包/无线依赖探测。Npcap 不随应用分发（许可证限制），只做检测 + 引导安装；
/// 用户可自行把 npcap 安装包放到程序目录的 redist\ 下，应用会调用静默安装。
/// </summary>
public static class DependencyProbe
{
    public const string NpcapDownloadUrl = "https://npcap.com/#download";

    public static string RedistDirectory => Path.Combine(AppContext.BaseDirectory, "redist");

    public static IReadOnlyList<DependencyStatus> Detect()
    {
        var list = new List<DependencyStatus>
        {
            DetectNpcap(),
            DetectWireshark(),
            DetectTcpdump(),
            DetectPktmon()
        };
        return list;
    }

    /// <summary>在 redist\ 下寻找 Npcap 安装包（文件名匹配 npcap*.exe）。</summary>
    public static string? FindBundledNpcapInstaller()
    {
        try
        {
            if (!Directory.Exists(RedistDirectory)) return null;
            return Directory.EnumerateFiles(RedistDirectory, "npcap*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static DependencyStatus DetectNpcap()
    {
        var dllCandidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"),
            Path.Combine(Environment.SystemDirectory, "wpcap.dll")
        };
        var dll = dllCandidates.FirstOrDefault(File.Exists);
        var driver = Path.Combine(Environment.SystemDirectory, "drivers", "npcap.sys");
        var bundled = FindBundledNpcapInstaller();

        var installed = dll is not null || File.Exists(driver);

        // 版本优先取注册表卸载项 DisplayVersion（Npcap 1.88 这类产品版本），
        // wpcap.dll 的 FileVersion 是 WinPcap API 版本（恒为 1.10.6），不能代表安装版本。
        var version = FindNpcapProductVersion();
        if (version is null && dll is not null)
        {
            try { version = FileVersionInfo.GetVersionInfo(dll).FileVersion; } catch { }
        }

        return new DependencyStatus(
            "npcap",
            "Npcap（WinPcap 兼容驱动）",
            installed,
            version,
            dll ?? (File.Exists(driver) ? driver : null),
            "802.11 monitor mode 原始抓帧、部分底层抓包能力（LLDP/CDP 也可用 Windows 内置 pktmon 替代）",
            installed
                ? "已安装。若需要抓 802.11 原始帧，请确认安装时勾选了 “Support raw 802.11 traffic (and monitor mode)”。"
                : "未安装。安装后可用 monitor mode（仍受网卡驱动限制）。注意：读取 AP 列表不需要 Npcap。",
            NpcapDownloadUrl,
            bundled is not null);
    }

    private static DependencyStatus DetectWireshark()
    {
        var (dir, version) = FindWiresharkInstall();
        var dumpcap = dir is null ? null : Path.Combine(dir, "dumpcap.exe");
        return new DependencyStatus(
            "dumpcap",
            "Wireshark / dumpcap",
            dumpcap is not null && File.Exists(dumpcap),
            version,
            dumpcap is not null && File.Exists(dumpcap) ? dumpcap : null,
            "LLDP/CDP 抓包后端之一（NetCable 只调用 dumpcap 抓包，不需要 tshark）",
            dir is null ? "未找到 Wireshark。可选安装（会自动带上 Npcap）。" : "已安装，NetCable 会自动识别（任意磁盘）。",
            "https://www.wireshark.org/download.html");
    }

    private static DependencyStatus DetectTcpdump()
    {
        var path = ProcessRunner.ResolveCommand("tcpdump");
        return new DependencyStatus(
            "tcpdump",
            "tcpdump",
            path is not null,
            null,
            path,
            "macOS/Linux 的抓包后端（Windows 一般不需要）",
            path is not null ? "可用" : "未安装（Windows 通常用 Npcap 或 dumpcap 即可）",
            null);
    }

    private static DependencyStatus DetectPktmon()
    {
        var path = Path.Combine(Environment.SystemDirectory, "pktmon.exe");
        var exists = File.Exists(path);
        return new DependencyStatus(
            "pktmon",
            "Windows 内置 pktmon",
            exists,
            null,
            exists ? path : null,
            "零安装的抓包后备方案（需管理员）；LLDP/CDP 可用它替代 Wireshark",
            exists ? "可用（需要管理员权限运行 NetCable）" : "当前系统不可用（Windows 10 1809+ 应自带）",
            null);
    }

    private static string? FindNpcapProductVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var view in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(view);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    var name = app?.GetValue("DisplayName") as string;
                    if (name is null || !name.Contains("Npcap", StringComparison.OrdinalIgnoreCase)) continue;
                    if (app!.GetValue("DisplayVersion") is string displayVersion && !string.IsNullOrWhiteSpace(displayVersion))
                        return displayVersion.Trim();
                }
            }
            catch { }
        }
        return null;
    }

    private static (string? Directory, string? Version) FindWiresharkInstall()
    {
        var probes = new List<(string Dir, string? Version)>();
        if (!OperatingSystem.IsWindows()) return (null, null);
        foreach (var view in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(view);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    var name = app?.GetValue("DisplayName") as string;
                    if (name is null || !name.Contains("Wireshark", StringComparison.OrdinalIgnoreCase)) continue;
                    var location = app!.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                        probes.Add((location.TrimEnd('\\'), app.GetValue("DisplayVersion") as string));
                }
            }
            catch { }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            foreach (var sub in new[] { @"Program Files\Wireshark", @"Program Files (x86)\Wireshark" })
            {
                try
                {
                    var dir = Path.Combine(drive.RootDirectory.FullName, sub);
                    if (Directory.Exists(dir)) probes.Add((dir, null));
                }
                catch { }
            }
        }

        var hit = probes.FirstOrDefault(p => File.Exists(Path.Combine(p.Dir, "dumpcap.exe")));
        return hit.Dir is null ? (null, null) : (hit.Dir, hit.Version);
    }

    /// <summary>调用 Npcap 安装包静默安装（需要管理员；会触发 UAC）。</summary>
    public static async Task<(bool Ok, string Message)> InstallNpcapAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(installerPath)) return (false, "安装包不存在：" + installerPath);

        var args = new[] { "/S", "/dot11_support=yes", "/admin_only=no" };
        var result = await Elevation.RunElevatedAsync(installerPath, args, timeoutMs: 180000, cancellationToken).ConfigureAwait(false);
        if (!result.Started)
            return (false, result.CanceledByUser ? "已取消授权（未获得管理员权限）" : (result.Error ?? "启动安装失败"));
        if (result.TimedOut) return (false, "安装超时（可能仍在后台进行，请稍后重新检测）");
        return result.ExitCode == 0
            ? (true, "安装程序已执行完毕，请点击「重新检测」确认")
            : (false, $"安装程序返回退出码 {result.ExitCode}。Npcap 静默安装参数为 /S /dot11_support=yes /admin_only=no");
    }
}
