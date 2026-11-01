using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Desktop.Services;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>依赖表中的一行（状态用颜色区分，由 View 根据 Installed 渲染）。</summary>
public sealed record DependencyRow(
    string Id,
    string Name,
    bool Installed,
    string? Version,
    string? Path,
    string Purpose,
    string? Hint)
{
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version!;

    public string PathText => string.IsNullOrWhiteSpace(Path) ? "—" : Path!;

    public string Detail => string.IsNullOrWhiteSpace(Hint) ? Purpose : $"{Purpose}　[{Hint}]";

    public string ShortName => Id switch
    {
        "npcap" => "Npcap",
        "dumpcap" => "Wireshark/dumpcap",
        "tcpdump" => "tcpdump",
        "pktmon" => "pktmon",
        _ => Name
    };
}

/// <summary>
/// 依赖与环境：检测 Npcap / Wireshark(dumpcap) / tcpdump / pktmon，
/// 提供安装引导与 redist 一键静默安装（不随应用分发 Npcap）。
/// </summary>
[ToolPage("deps")]
public sealed partial class DepsViewModel : ToolViewModelBase
{
    public ObservableCollection<DependencyRow> Dependencies { get; } = new();

    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _installHint = string.Empty;
    [ObservableProperty] private bool _canInstallNpcap;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _redistPath = string.Empty;
    [ObservableProperty] private string _privilegeText = string.Empty;

    public DepsViewModel() : base("deps")
    {
        RedistPath = DependencyProbe.RedistDirectory;
        PrivilegeText = $"当前进程权限：{(Environment.IsPrivilegedProcess ? "管理员（已提权）" : "标准用户 —— 抓包 / 安装 / WLAN API 读取需要管理员权限")}";

        DetectDependencies(log: false);
        UpdateInstallAvailability();
    }

    partial void OnIsInstallingChanged(bool value) => UpdateInstallAvailability();

    [RelayCommand]
    private void OpenDownloadPage()
    {
        var url = DependencyProbe.NpcapDownloadUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogInfo($"已在系统浏览器打开 Npcap 下载页：{url}");
            StatusText = "已打开下载页";
        }
        catch (Exception ex)
        {
            LogError($"无法打开浏览器（{url}）：{ex.Message}");
            StatusText = "打开下载页失败";
        }
    }

    [RelayCommand]
    private async Task InstallNpcapAsync()
    {
        if (IsInstalling) return;

        var installer = DependencyProbe.FindBundledNpcapInstaller();
        if (installer is null)
        {
            LogWarn($"未找到随附安装包：请把 npcap-x.y.z.exe 放到 {DependencyProbe.RedistDirectory}");
            StatusText = "缺少安装包";
            return;
        }

        var owner = GetOwnerWindow();
        var confirmed = await Dialogs.ConfirmAsync(
            owner,
            "静默安装 Npcap",
            $"将调用 Npcap 官方安装包执行静默安装：\n\n{installer}\n\n参数：/S /dot11_support=yes /admin_only=no\n\n" +
            "这会触发 UAC（用户账户控制），请在弹出的系统窗口中允许管理员权限。\n" +
            "Npcap 对个人/教育用途免费；随应用再分发需要 Nmap 的 Npcap OEM 授权（NetCable 不随应用分发）。",
            okText: "安装",
            cancelText: "取消").ConfigureAwait(true);

        if (!confirmed)
        {
            LogMuted("已取消 Npcap 安装");
            StatusText = "已取消";
            return;
        }

        IsInstalling = true;
        StatusText = "正在安装（等待 UAC 授权）…";
        LogInfo($"开始静默安装 {Path.GetFileName(installer)}（/S /dot11_support=yes /admin_only=no）");

        try
        {
            var (ok, message) = await DependencyProbe
                .InstallNpcapAsync(installer, CancellationToken.None)
                .ConfigureAwait(true);

            if (ok) LogOk(message);
            else LogError(message);

            StatusText = ok ? "安装完成，正在重新检测…" : "安装失败";
            DetectionSummaryPart();
            DetectDependencies(log: true);
        }
        catch (Exception ex)
        {
            LogError("安装异常：" + ex.Message);
            StatusText = "安装失败";
        }
        finally
        {
            IsInstalling = false;
            UpdateInstallAvailability();
        }
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        LogInfo($"重新检测抓包依赖（{DateTimeOffset.Now:HH:mm:ss}）");
        DetectDependencies(log: true);
        return Task.CompletedTask;
    }

    private void DetectDependencies(bool log)
    {
        try
        {
            var statuses = DependencyProbe.Detect();
            Dependencies.Clear();
            foreach (var status in statuses)
            {
                Dependencies.Add(new DependencyRow(
                    status.Id,
                    status.Name,
                    status.Installed,
                    status.Version,
                    status.Path,
                    status.Purpose,
                    status.Hint));
            }

            UpdateSummary();

            if (!log) return;

            AddRowIfAny();

            foreach (var status in statuses)
            {
                var detail = $"{status.Name}：{(status.Installed ? "已安装" : "未安装")}";
                if (!string.IsNullOrWhiteSpace(status.Version)) detail += $" · 版本 {status.Version}";
                if (!string.IsNullOrWhiteSpace(status.Path)) detail += $" · {status.Path}";

                if (status.Installed) LogOk(detail);
                else LogWarn(detail + (string.IsNullOrWhiteSpace(status.Hint) ? string.Empty : " · " + status.Hint));
            }

            LogInfo($"进程权限：{(Environment.IsPrivilegedProcess ? "管理员" : "标准用户（抓包 / 安装需要管理员权限）")}");
            LogInfo($"redist 目录：{DependencyProbe.RedistDirectory}（" +
                    (DependencyProbe.FindBundledNpcapInstaller() is { } installer
                        ? "已发现安装包 " + Path.GetFileName(installer)
                        : "未发现 npcap*.exe") + "）");
        }
        catch (Exception ex)
        {
            Summary = "检测失败：" + ex.Message;
            LogError(Summary);
        }

        UpdateInstallAvailability();
    }

    /// <summary>把本次检测结果同步到 Rows（供需要表格化输出的场景/测试查看）。</summary>
    private void AddRowIfAny()
    {
        Rows.Clear();
        AddRow("检测时间", DateTimeOffset.Now.ToString("HH:mm:ss"), $"{Dependencies.Count(d => d.Installed)}/{Dependencies.Count} 项已就绪");
        AddRow("redist 目录", DependencyProbe.RedistDirectory,
            DependencyProbe.FindBundledNpcapInstaller() is { } installer
                ? "已发现安装包 " + Path.GetFileName(installer)
                : "未发现 npcap*.exe");
        AddRow("进程权限", Environment.IsPrivilegedProcess ? "管理员" : "标准用户", "抓包 / 安装需要管理员权限");
    }

    private void UpdateSummary()
    {
        if (Dependencies.Count == 0)
        {
            Summary = "未检测到依赖信息";
            return;
        }

        var installed = Dependencies.Count(d => d.Installed);
        var items = Dependencies.Select(d => $"{d.ShortName} {(d.Installed ? "✅" : "❌")}");
        Summary = $"{installed}/{Dependencies.Count} 项依赖已就绪 · {string.Join(" · ", items)}";
    }

    private void UpdateInstallAvailability()
    {
        var installer = DependencyProbe.FindBundledNpcapInstaller();
        CanInstallNpcap = installer is not null && !IsInstalling;

        InstallHint = installer is null
            ? "未找到随附安装包：将 npcap-x.y.z.exe 放到程序目录的 redist\\ 下，此按钮即可一键静默安装（会触发 UAC）。"
            : $"已找到安装包 {Path.GetFileName(installer)}：点击「静默安装 Npcap」将触发 UAC，并以 /S /dot11_support=yes /admin_only=no 安装。";
    }

    private void DetectionSummaryPart()
    {
        // 安装后先刷新摘要，避免等待下一次检测的空窗。
        UpdateSummary();
    }

    private static Window? GetOwnerWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

