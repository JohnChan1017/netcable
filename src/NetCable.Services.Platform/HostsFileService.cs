using System.Globalization;
using System.Net;
using System.Text;
using NetCable.Core.Contracts;
using NetCable.Core.Models;

namespace NetCable.Services.Platform;

/// <summary>
/// hosts 文件只读/写入服务。
/// Windows 路径为 <c>%SystemRoot%\System32\drivers\etc\hosts</c>，其余平台为 <c>/etc/hosts</c>。
/// 读取解析失败返回空列表；写入前自动备份，并使用“临时文件 + 原子替换”避免半写文件。
/// 无写权限时 <see cref="WriteAsync"/> 会抛出 <see cref="UnauthorizedAccessException"/> 交由 UI 提示。
/// </summary>
public sealed class HostsFileService : IHostsFileService
{
    private static readonly string HostsPath = ResolveHostsPath();

    /// <summary>读取 hosts 文件信息（路径、是否存在、是否可写、有效条目数）。</summary>
    public async Task<HostsFileInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = HostsPath;

        try
        {
            var exists = File.Exists(path);
            var canWrite = CanWrite(path);
            var entryCount = 0;
            if (exists && canWrite)
            {
                entryCount = (await ReadAsync(cancellationToken).ConfigureAwait(false)).Count;
            }
            else if (exists)
            {
                try
                {
                    entryCount = ParseEntries(await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).Count;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    entryCount = 0;
                }
            }

            return new HostsFileInfo(path, exists, canWrite, entryCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HostsFileInfo(path, false, false, 0);
        }
    }

    /// <summary>逐行读取 hosts 的有效映射（忽略空行与纯注释行；一行多个主机名会展开成多条记录）。</summary>
    public async Task<IReadOnlyList<HostsEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var path = HostsPath;
            if (!File.Exists(path)) return Array.Empty<HostsEntry>();

            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            return ParseEntries(lines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<HostsEntry>();
        }
    }

    /// <summary>
    /// 备份当前 hosts 到 <c>&lt;path&gt;.netcable-backup-yyyyMMdd-HHmmss</c>，返回备份文件路径。
    /// 文件不存在时抛 <see cref="FileNotFoundException"/>，无权限时抛 <see cref="UnauthorizedAccessException"/>。
    /// </summary>
    public Task<string> BackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = HostsPath;
        if (!File.Exists(path)) throw new FileNotFoundException($"hosts 文件不存在：{path}", path);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backupPath = $"{path}.netcable-backup-{timestamp}";
        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{path}.netcable-backup-{timestamp}-{suffix++}";
        }

        File.Copy(path, backupPath, overwrite: false);
        return Task.FromResult(backupPath);
    }

    /// <summary>
    /// 写入 hosts：先自动备份，再以“临时文件 + File.Replace/Move”原子替换。
    /// 每行格式为 <c>ip\t主机名\t# 注释</c>；无权限时抛出 <see cref="UnauthorizedAccessException"/>。
    /// </summary>
    public async Task WriteAsync(IReadOnlyList<HostsEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();

        var path = HostsPath;
        var content = BuildContent(entries, cancellationToken);

        if (File.Exists(path))
        {
            await BackupAsync(cancellationToken).ConfigureAwait(false);
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) directory = ".";

        var tempPath = Path.Combine(directory, $".netcable-hosts-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 清理临时文件失败不影响主流程
                }
            }
        }
    }

    private static string BuildContent(IReadOnlyList<HostsEntry> entries, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ip = entry.IpAddress?.Trim() ?? string.Empty;
            var hostName = entry.HostName?.Trim() ?? string.Empty;
            if (ip.Length == 0 || hostName.Length == 0) continue;

            if (!IPAddress.TryParse(ip, out _))
            {
                throw new FormatException($"无效的 IP 地址：{ip}");
            }

            if (hostName.Contains(' ') || hostName.Contains('\t') || hostName.Contains('#'))
            {
                throw new FormatException($"无效的主机名：{hostName}");
            }

            builder.Append(ip).Append('\t').Append(hostName);

            var comment = entry.Comment?.Trim();
            if (!string.IsNullOrWhiteSpace(comment))
            {
                comment = comment.Replace('\r', ' ').Replace('\n', ' ');
                builder.Append('\t').Append("# ").Append(comment);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IReadOnlyList<HostsEntry> ParseEntries(IReadOnlyList<string> lines)
    {
        var result = new List<HostsEntry>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            string? comment = null;
            var content = trimmed;
            var commentIndex = content.IndexOf('#');
            if (commentIndex >= 0)
            {
                var commentText = content[(commentIndex + 1)..].Trim();
                comment = commentText.Length == 0 ? null : commentText;
                content = content[..commentIndex].Trim();
            }

            var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            // 第一列必须是合法 IP，否则视为不可解析行并跳过（避免写回时污染文件）
            if (!IPAddress.TryParse(tokens[0], out _)) continue;

            for (var token = 1; token < tokens.Length; token++)
            {
                var hostName = tokens[token].Trim();
                if (hostName.Length == 0) continue;
                result.Add(new HostsEntry(tokens[0], hostName, comment, index + 1));
            }
        }

        return result;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                return stream.CanWrite;
            }

            // 文件不存在时探测所在目录是否可创建文件
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

            var probePath = Path.Combine(directory, $".netcable-write-test-{Guid.NewGuid():N}");
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            return probe.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveHostsPath()
    {
        if (!OperatingSystem.IsWindows()) return "/etc/hosts";

        try
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\Windows";
            }

            return Path.Combine(systemRoot, "System32", "drivers", "etc", "hosts");
        }
        catch
        {
            return @"%WINDIR%\System32\drivers\etc\hosts";
        }
    }
}
