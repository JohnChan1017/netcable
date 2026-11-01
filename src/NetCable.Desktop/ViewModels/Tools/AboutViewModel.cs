using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Platform;
using NetCable.Desktop.Services;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>关于与更新：版本信息、检查 GitHub Releases、下载安装与未签名提示。</summary>
[ToolPage("about")]
public sealed partial class AboutViewModel : ToolViewModelBase
{
    private UpdateInfo? _update;
    private CancellationTokenSource? _downloadCts;

    public AboutViewModel() : base("about")
    {
        VersionText = "v" + UpdateService.CurrentVersionText;
        PlatformText = $"{PlatformInfo.DisplayName} {PlatformInfo.Architecture} · .NET {Environment.Version}";
        RepoUrl = $"https://github.com/{UpdateService.RepoOwner}/{UpdateService.RepoName}";
        Idle = true;
        StatusText = "就绪";
        Summary = "检查 GitHub Releases 以获取最新版本";
    }

    public string VersionText { get; }
    public string PlatformText { get; }
    public string RepoUrl { get; }

    [ObservableProperty] private bool _idle = true;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _isUpToDate;
    [ObservableProperty] private bool _checkFailed;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _updateHeadline = string.Empty;
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private string _notesText = string.Empty;
    [ObservableProperty] private string _summary = "—";

    [RelayCommand]
    private void OpenReleasesPage()
    {
        OpenUrl(UpdateService.ReleasesUrl);
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        OpenUrl(_update?.ReleaseUrl ?? UpdateService.LatestReleaseUrl);
    }

    [RelayCommand]
    private void OpenRepoPage()
    {
        OpenUrl(RepoUrl);
    }

    [RelayCommand]
    private void CancelAll()
    {
        _downloadCts?.Cancel();
        if (IsRunning) CancelCommand.Execute(null);
        StatusText = "正在停止…";
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (IsDownloading) return;

        if (_update is null)
        {
            LogWarn("请先检查更新");
            return;
        }

        var asset = _update.PreferredAsset;
        if (asset is null)
        {
            LogWarn("该 Release 没有可下载的安装包（无 MSI / ZIP 资产）");
            Summary = "该 Release 没有可下载的安装包";
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        IsBusy = true;
        CanDownload = false;
        Progress = 0;
        DownloadStatus = "准备下载…";
        StatusText = "下载中…";
        Summary = $"下载 {asset.Name}（{FormatSize(asset.Size)}）";
        LogInfo($"开始下载 {asset.Name}（{FormatSize(asset.Size)}）");

        try
        {
            var progress = new Progress<double>(value =>
            {
                Progress = value;
                DownloadStatus = $"下载中 {value:0}%";
            });

            var (path, error) = await UpdateService
                .DownloadAsync(asset, progress, _downloadCts.Token)
                .ConfigureAwait(true);

            if (error is not null)
            {
                LogError(error);
                DownloadStatus = error;
                StatusText = "下载失败";
                Summary = error;
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                LogWarn("下载未返回文件路径");
                DownloadStatus = "下载失败";
                StatusText = "下载失败";
                return;
            }

            LogOk($"下载完成：{path}");
            DownloadStatus = "下载完成，正在启动安装…";
            StatusText = "安装中…";

            var (ok, message) = await UpdateService
                .InstallAsync(path, _downloadCts.Token)
                .ConfigureAwait(true);

            if (ok) LogOk(message);
            else LogError(message);

            DownloadStatus = message;
            StatusText = ok ? "安装完成" : "安装失败";
            Summary = message;
            AddRow("安装包", asset.Name, FormatSize(asset.Size));
            AddRow("本地路径", path);
        }
        catch (OperationCanceledException)
        {
            LogMuted("已取消下载 / 安装");
            DownloadStatus = "已取消";
            StatusText = "已取消";
            Summary = "已取消";
            Progress = 0;
        }
        catch (Exception ex)
        {
            LogError("下载 / 安装异常：" + ex.Message);
            DownloadStatus = "失败：" + ex.Message;
            StatusText = "失败";
            Summary = "下载 / 安装失败";
        }
        finally
        {
            IsDownloading = false;
            IsBusy = IsRunning;
            CanDownload = HasUpdate;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        ResetState();
        IsBusy = true;
        StatusText = "正在检查更新…";
        Summary = "正在访问 api.github.com …";
        LogInfo($"检查更新：GET api.github.com/repos/{UpdateService.RepoOwner}/{UpdateService.RepoName}/releases/latest");

        try
        {
            var (info, error) = await UpdateService.CheckAsync(cancellationToken).ConfigureAwait(true);

            if (error is not null)
            {
                CheckFailed = true;
                ErrorMessage = BuildErrorMessage(error);
                StatusText = "检查失败";
                Summary = ErrorMessage;
                LogWarn("检查更新失败：" + error);

                if (error.Contains("尚未发布", StringComparison.Ordinal))
                    LogInfo("仓库还没有发布任何 Release 属于正常状态；发布首个版本后这里会自动提示更新。");
                else if (error.Contains("SSL", StringComparison.OrdinalIgnoreCase) || error.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                    LogWarn("本机 HTTPS 连接不可用（SChannel / 证书链受限），更新检查需要能访问 https://api.github.com。");

                ShowResult = true;
                AddRow("当前版本", VersionText);
                AddRow("检查结果", error);
                AddRow("仓库", UpdateService.ReleasesUrl);
                return;
            }

            if (info is null)
            {
                CheckFailed = true;
                ErrorMessage = "未获取到更新信息";
                StatusText = "检查失败";
                Summary = ErrorMessage;
                LogError(ErrorMessage);
                ShowResult = true;
                AddRow("当前版本", VersionText);
                AddRow("检查结果", ErrorMessage);
                return;
            }

            _update = info;
            ShowResult = true;
            AddRow("当前版本", VersionText);
            AddRow("最新版本", "v" + info.LatestVersion, info.Tag);
            AddRow("发布时间", info.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—");
            var asset = info.PreferredAsset;
            AddRow("安装包", asset?.Name ?? "无（该 Release 没有资产）",
                asset is null ? string.Empty : FormatSize(asset.Size));

            if (!info.IsNewer)
            {
                IsUpToDate = true;
                StatusText = "已是最新";
                Summary = $"已是最新版本（v{info.LatestVersion}）";
                LogOk(Summary);
                return;
            }

            HasUpdate = true;
            CanDownload = asset is not null;
            UpdateHeadline = $"发现新版本 v{info.LatestVersion}";
            NotesText = string.IsNullOrWhiteSpace(info.Notes) ? "（该 Release 没有 Release Notes）" : info.Notes.Trim();
            StatusText = "发现新版本";
            Summary = $"发现新版本 v{info.LatestVersion}" +
                      (info.PublishedAt is { } published ? $" · 发布于 {published.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty) +
                      (asset is null ? " · 无安装包资产" : $" · {asset.Name}");
            LogOk(Summary);
        }
        finally
        {
            IsBusy = IsDownloading;
        }
    }

    private void ResetState()
    {
        Idle = false;
        HasUpdate = false;
        IsUpToDate = false;
        CheckFailed = false;
        CanDownload = false;
        ShowResult = false;
        ErrorMessage = string.Empty;
        UpdateHeadline = string.Empty;
        NotesText = string.Empty;
        DownloadStatus = string.Empty;
        _update = null;
    }

    private static string BuildErrorMessage(string error)
    {
        if (error.Contains("SSL", StringComparison.OrdinalIgnoreCase) || error.Contains("TLS", StringComparison.OrdinalIgnoreCase))
            return error + "\n（本机 HTTPS 连接不可用：SChannel / 证书链受限，更新检查需要访问 https://api.github.com）";
        if (error.Contains("尚未发布", StringComparison.Ordinal))
            return error + "\n（正常状态：仓库发布首个 Release 后即可检查到更新）";
        return error;
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogInfo("已在系统浏览器打开：" + url);
            StatusText = "已打开浏览器";
        }
        catch (Exception ex)
        {
            LogError($"无法打开浏览器（{url}）：{ex.Message}");
            StatusText = "打开链接失败";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }
}


