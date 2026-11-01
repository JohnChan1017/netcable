using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetCable.Core.Platform;

namespace NetCable.Desktop.Services;

public sealed record UpdateAsset(string Name, string Url, long Size, string ContentType);

public sealed record UpdateInfo(
    bool IsNewer,
    string LatestVersion,
    string Tag,
    string ReleaseUrl,
    string Notes,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<UpdateAsset> Assets)
{
    public UpdateAsset? PreferredAsset
    {
        get
        {
            // Windows: 优先 MSI，其次 portable.zip
            var msi = Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                      ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            if (msi is not null) return msi;

            var zip = Assets.FirstOrDefault(a => a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            return zip ?? Assets.FirstOrDefault();
        }
    }
}

/// <summary>
/// 自动更新：查询 GitHub Releases 最新版本 → 下载安装包 → 调用安装程序。
/// 仓库地址在 <see cref="RepoOwner"/>/<see cref="RepoName"/> 配置。
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "JohnChan1017";
    public const string RepoName = "netcable";

    private static readonly HttpClient Http = CreateClient();

    public static string ReleasesUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";
    public static string LatestReleaseUrl => $"{ReleasesUrl}/latest";

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NetCable/{CurrentVersionText} (+https://github.com/{RepoOwner}/{RepoName})");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>查询最新 Release；网络失败/仓库没有 Release 时返回 Error。</summary>
    public static async Task<(UpdateInfo? Info, string? Error)> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, "仓库尚未发布任何 Release");
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub API 返回 {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? LatestReleaseUrl : LatestReleaseUrl;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
            DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var dt) ? dt : null;

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var assetName = a.TryGetProperty("name", out var an) ? an.GetString() ?? string.Empty : string.Empty;
                    var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    var ct = a.TryGetProperty("content_type", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrEmpty(assetName) && !string.IsNullOrEmpty(dl))
                        assets.Add(new UpdateAsset(assetName, dl, size, ct));
                }
            }

            var latest = ParseVersion(tag) ?? ParseVersion(name) ?? CurrentVersion;
            var isNewer = latest > Normalize(CurrentVersion);

            return (new UpdateInfo(isNewer, latest.ToString(3), tag, htmlUrl, notes, published, assets), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "检查更新失败：" + ex.Message);
        }
    }

    /// <summary>下载资产到临时目录，返回本地路径。</summary>
    public static async Task<(string? Path, string? Error)> DownloadAsync(
        UpdateAsset asset, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "NetCableUpdate");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, asset.Name);

            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.Create(target);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report(Math.Clamp(read * 100.0 / total, 0, 100));
            }
            progress?.Report(100);
            return (target, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "下载失败：" + ex.Message);
        }
    }

    /// <summary>执行安装：MSI 走 msiexec（必要时提权），ZIP 提示手动解压。</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path)) return (false, "安装包不存在：" + path);

            if (path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && PlatformInfo.IsWindows)
            {
                // 先尝试普通权限（perUser MSI 不需要提权）
                var result = await ProcessRunner.RunAsync("msiexec", new[] { "/i", path, "/qb" }, 300000, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0) return (true, "安装完成，请重启 NetCable 生效。");

                // 1603/1925 通常是权限不足 → 提权重试
                var elevated = await Elevation.RunElevatedAsync("msiexec.exe", new[] { "/i", path, "/qb" }, 300000, cancellationToken).ConfigureAwait(false);
                if (!elevated.Started)
                    return (false, elevated.CanceledByUser ? "已取消授权（未获得管理员权限）" : (elevated.Error ?? "启动安装失败"));
                if (elevated.TimedOut) return (false, "安装超时");
                return elevated.ExitCode == 0
                    ? (true, "安装完成，请重启 NetCable 生效。")
                    : (false, $"安装程序返回退出码 {elevated.ExitCode}（首次错误：{result.CombinedOutput.Trim()}）");
            }

            // 非 MSI：打开所在目录，让用户手动替换
            if (PlatformInfo.IsWindows)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (PlatformInfo.IsMacOS)
                await ProcessRunner.RunAsync("open", new[] { "-R", path }, 10000, cancellationToken).ConfigureAwait(false);
            else
                Process.Start(new ProcessStartInfo("xdg-open", Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });

            return (true, "已下载并打开所在目录，请手动解压/安装。");
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message);
        }
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = text.Trim().TrimStart('v', 'V');
        var core = new string(cleaned.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
        return Version.TryParse(core, out var v) ? Normalize(v) : null;
    }
}
