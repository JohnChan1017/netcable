using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NetCable.Core.Contracts;
using NetCable.Core.Models;
using NetCable.Core.Platform;

namespace NetCable.Services.Network;

/// <summary>
/// LLDP / CDP 邻居发现服务。后端优先级：<b>Npcap 直连</b> → <b>Wireshark dumpcap</b> →
/// <b>tcpdump</b> → <b>Windows pktmon</b>（内置后备，需管理员）。
/// <para>
/// 设计取舍：
/// <list type="bullet">
///   <item>Npcap 直连：动态加载 <c>wpcap.dll</c> 实时抓帧，不依赖 Wireshark，权限与非管理员 dumpcap 一致。</item>
///   <item>dumpcap：抓包写临时文件（<c>-P</c> 强制经典 pcap）再解析，避免二进制 stdout 丢字节。</item>
///   <item>tcpdump：抓包写临时文件，到时间后由本服务结束进程（<c>-U</c> 保证数据及时落盘）。</item>
///   <item>pktmon：<c>etl2pcap</c> 转换后复用同一 pcap 解析器，零第三方依赖但需要管理员权限。</item>
///   <item>工具探测覆盖 PATH、注册表 Uninstall 的 InstallLocation、Program Files 以及所有固定磁盘的
///         <c>\Program Files\Wireshark\dumpcap.exe</c>（Wireshark 可能装在 D 盘等非系统盘）。</item>
///   <item>单包解析异常跳过；进程失败/超时返回空流，原因写入 <see cref="LastError"/>，不抛异常。</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class DiscoveryProtocolService : IDiscoveryProtocolService
{
    private const uint LinkTypeEthernet = 1;
    private const uint LinkTypeLinuxSll = 113;
    private const uint LinkTypeLinuxSll2 = 276;

    private const int MaxPacketLength = 262_144;
    private const long MaxCaptureBytes = 64L * 1024 * 1024;
    private const int FallbackDurationGraceSeconds = 25;
    private const int MaxDurationSeconds = 60;

    private const string NoNeighborHint =
        "抓包完成，但未发现 LLDP/CDP 邻居（对端设备可能未启用邻居发现协议）。";

    private const string UnsupportedReason =
        "未找到可用抓包后端：Npcap 直连不可用，也未找到 dumpcap / tcpdump / pktmon。" +
        "请安装 Npcap（推荐，无需 Wireshark）或 Wireshark；macOS/Linux 可用包管理器安装 tcpdump 后重试。";

    private static readonly string[] DumpcapRelativeToDrivePaths =
    {
        @"Program Files\Wireshark\dumpcap.exe",
        @"Program Files (x86)\Wireshark\dumpcap.exe",
        @"Wireshark\dumpcap.exe"
    };

    private static readonly string[] DumpcapFixedCandidates =
    {
        @"C:\Program Files\Wireshark\dumpcap.exe",
        @"C:\Program Files (x86)\Wireshark\dumpcap.exe",
        @"C:\Program Files\Npcap\dumpcap.exe",
        "/Applications/Wireshark.app/Contents/MacOS/dumpcap",
        "/opt/homebrew/bin/dumpcap",
        "/usr/local/bin/dumpcap",
        "/usr/bin/dumpcap",
        "/usr/sbin/dumpcap"
    };

    private static readonly string[] TcpdumpCandidates =
    {
        "/usr/sbin/tcpdump",
        "/usr/bin/tcpdump",
        "/sbin/tcpdump",
        "/opt/homebrew/bin/tcpdump",
        "/usr/local/bin/tcpdump"
    };

    [GeneratedRegex(@"^\s*(?:\[)?(?<index>\d+)(?:\]|\.)\s+(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DumpcapInterfaceRegex();

    /// <summary>
    /// 最近一次抓包的失败原因（成功时为 null；抓包成功但没有邻居时是提示文本，不是错误）。
    /// 接口本身只返回邻居流，UI 可在此属性读取“为什么没有结果”。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 最近一次能力探测/抓包实际使用的后端：
    /// <c>"Npcap 直连"</c> / <c>"Wireshark dumpcap"</c> / <c>"tcpdump"</c> / <c>"Windows pktmon"</c> / null。
    /// </summary>
    public string? LastBackend { get; private set; }

    /// <summary>
    /// 最近一次抓包的统计摘要，例如 <c>"抓包 5s，共 812 帧，其中 LLDP 0 / CDP 0"</c>。
    /// UI 用它解释“抓包成功但没有邻居”是正常现象。
    /// </summary>
    public string? LastCaptureStats { get; private set; }

    /// <inheritdoc />
    public Task<(bool Supported, string? Reason)> GetCapabilityAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            LastBackend = null;
            LastCaptureStats = null;

            // 1) Npcap 直连（Windows）：用户装了 Npcap 即可用，不需要 Wireshark
            if (OperatingSystem.IsWindows())
            {
                var npcap = NpcapDirectBackend.TryGet(out _);
                if (npcap is not null)
                {
                    var device = npcap.SelectDevice(out _);
                    if (device is not null)
                    {
                        LastBackend = NpcapDirectBackend.BackendName;
                        return Task.FromResult<(bool, string?)>((true,
                            $"{NpcapDirectBackend.BackendName}（{npcap.LibraryPath}），设备：{device.DisplayName}"));
                    }
                }
            }

            // 2) dumpcap（Wireshark）
            var tool = FindCaptureTool();
            if (tool is not null)
            {
                LastBackend = tool.Value.BackendName;
                return Task.FromResult<(bool, string?)>((true, $"{tool.Value.KindName}：{tool.Value.Path}"));
            }

            // 3) pktmon（Windows 内置，需管理员）
            if (PktmonCapture.IsAvailable(out var pktmonPath))
            {
                LastBackend = PktmonCapture.BackendName;
                return Task.FromResult<(bool, string?)>((true,
                    $"{PktmonCapture.BackendName}：{pktmonPath}（Windows 10 1809+ 内置，需要管理员权限）"));
            }

            LastBackend = null;
            return Task.FromResult<(bool, string?)>((false, UnsupportedReason));
        }
        catch (Exception ex)
        {
            LastBackend = null;
            return Task.FromResult<(bool, string?)>((false, $"抓包能力探测失败：{ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DiscoveryNeighbor> CaptureAsync(
        int durationSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastError = null;
        LastBackend = null;
        LastCaptureStats = null;
        durationSeconds = Math.Clamp(durationSeconds, 1, MaxDurationSeconds);

        var errors = new List<string>();
        string? pcapFile = null;

        try
        {
            // ========== 1) Npcap 直连 ==========
            if (OperatingSystem.IsWindows())
            {
                NpcapDirectBackend? npcap = null;
                try { npcap = NpcapDirectBackend.TryGet(out _); }
                catch (Exception ex) { errors.Add($"Npcap 直连不可用：{ex.Message}"); }

                if (npcap is not null)
                {
                    NpcapDevice? device = null;
                    try { device = npcap.SelectDevice(out _); }
                    catch (Exception ex) { errors.Add($"Npcap 选择网卡失败：{ex.Message}"); }

                    if (device is not null)
                    {
                        NpcapCaptureResult? npcapResult = null;
                        string? npcapCaptureError = null;
                        try
                        {
                            npcapResult = npcap.Capture(
                                device, durationSeconds, device.DisplayName, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            npcapCaptureError = $"Npcap 直连抓包失败：{ex.Message}";
                        }

                        if (npcapResult is not null && (npcapResult.Frames > 0 || npcapResult.Error is null))
                        {
                            LastBackend = NpcapDirectBackend.BackendName;
                            LastCaptureStats = FormatStats(
                                durationSeconds, npcapResult.Frames, npcapResult.LldpFrames, npcapResult.CdpFrames);
                            LastError = npcapResult.Error;
                            if (LastError is null && npcapResult.Neighbors.Count == 0) LastError = NoNeighborHint;

                            foreach (var neighbor in MergeNeighbors(npcapResult.Neighbors))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                yield return neighbor;
                            }
                            yield break;
                        }

                        errors.Add(npcapResult?.Error ?? npcapCaptureError ?? "Npcap 直连未捕获到数据帧");
                    }
                    else
                    {
                        errors.Add("Npcap 直连未找到可用抓包设备");
                    }
                }
            }

            // ========== 2) dumpcap / tcpdump ==========
            CaptureTool? tool = null;
            try { tool = FindCaptureTool(); }
            catch (Exception ex) { errors.Add($"查找抓包工具失败：{ex.Message}"); }

            if (tool is not null)
            {
                var captureTool = tool.Value;
                CaptureInterface? captureInterface = null;
                try
                {
                    captureInterface = await SelectInterfaceAsync(captureTool, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"选择抓包网卡失败：{ex.Message}");
                }

                if (captureInterface is not null)
                {
                    pcapFile = await CaptureToFileAsync(
                            captureTool, captureInterface.Value.Name, durationSeconds, cancellationToken)
                        .ConfigureAwait(false);

                    if (pcapFile is not null)
                    {
                        var parsed = await ParsePcapFileAsync(
                                pcapFile, captureInterface.Value.Label, cancellationToken)
                            .ConfigureAwait(false);
                        TryDelete(pcapFile);
                        pcapFile = null;

                        if (parsed.Error is null)
                        {
                            LastBackend = captureTool.BackendName;
                            LastCaptureStats = FormatStats(
                                durationSeconds, parsed.FrameCount, parsed.LldpCount, parsed.CdpCount);
                            LastError = parsed.Neighbors.Count == 0 ? NoNeighborHint : null;

                            foreach (var neighbor in parsed.Neighbors)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                yield return neighbor;
                            }
                            yield break;
                        }

                        errors.Add(parsed.Error);
                    }
                    else if (LastError is not null)
                    {
                        errors.Add(LastError);
                    }
                }
                else
                {
                    errors.Add(LastError ?? $"未找到 {captureTool.KindName} 可用网卡");
                }
            }

            // ========== 3) Windows pktmon 后备 ==========
            if (PktmonCapture.IsAvailable(out _))
            {
                string? pktmonPcap = null;
                string? pktmonError = null;
                try
                {
                    (pktmonPcap, pktmonError) = await PktmonCapture
                        .CaptureToPcapAsync(durationSeconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    pktmonError = $"Windows pktmon 抓包失败：{ex.Message}";
                }

                if (pktmonPcap is not null)
                {
                    PcapParseResult parsed;
                    try
                    {
                        parsed = await ParsePcapFileAsync(
                                pktmonPcap, PktmonCapture.BackendName, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        TryDelete(pktmonPcap);
                    }

                    if (parsed.Error is null)
                    {
                        LastBackend = PktmonCapture.BackendName;
                        LastCaptureStats = FormatStats(
                            durationSeconds, parsed.FrameCount, parsed.LldpCount, parsed.CdpCount);
                        LastError = parsed.Neighbors.Count == 0 ? NoNeighborHint : null;

                        foreach (var neighbor in parsed.Neighbors)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return neighbor;
                        }
                        yield break;
                    }

                    errors.Add(parsed.Error);
                }

                if (pktmonError is not null) errors.Add(pktmonError);
            }

            LastError = errors.Count > 0
                ? string.Join("；", errors.Distinct(StringComparer.Ordinal))
                : UnsupportedReason;
        }
        finally
        {
            if (pcapFile is not null) TryDelete(pcapFile);
        }
    }

    private static string FormatStats(int durationSeconds, int frames, int lldpFrames, int cdpFrames) =>
        $"抓包 {durationSeconds}s，共 {frames} 帧，其中 LLDP {lldpFrames} / CDP {cdpFrames}";

    private static List<DiscoveryNeighbor> MergeNeighbors(IReadOnlyList<DiscoveryNeighbor> rawNeighbors)
    {
        var merged = new List<DiscoveryNeighbor>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var neighbor in rawNeighbors) MergeNeighbor(merged, seen, neighbor);
        return merged;
    }

    // ------------------------------------------------------------------ 抓包工具与网卡

    private static CaptureTool? FindCaptureTool()
    {
        var dumpcapPath = FindDumpcapPath();
        if (dumpcapPath is not null) return new CaptureTool(dumpcapPath, true);

        var tcpdumpPath = FindTcpdumpPath();
        if (tcpdumpPath is not null) return new CaptureTool(tcpdumpPath, false);
        return null;
    }

    private static string? FindDumpcapPath()
    {
        // 1) PATH
        var onPath = ProcessRunner.ResolveCommand("dumpcap");
        if (!string.IsNullOrEmpty(onPath) && SafeFileExists(onPath)) return onPath;

        // 2) 注册表 Uninstall 的 InstallLocation（Wireshark 可能装在 D 盘等非系统盘）
        if (OperatingSystem.IsWindows())
        {
            foreach (var location in GetWiresharkInstallLocations())
            {
                var candidate = SafeCombine(location, "dumpcap.exe");
                if (candidate is not null && SafeFileExists(candidate)) return candidate;
            }
        }

        // 3) Program Files 常见目录 + macOS/Linux 固定路径
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var relative in new[] { "Wireshark", "Npcap" })
            {
                var candidate = SafeCombine(root, relative, "dumpcap.exe");
                if (candidate is not null && SafeFileExists(candidate)) return candidate;
            }
        }
        foreach (var candidate in DumpcapFixedCandidates)
        {
            if (SafeFileExists(candidate)) return candidate;
        }

        // 4) 所有固定磁盘（过滤 Fixed/Ready）
        foreach (var driveRoot in GetFixedDriveRoots())
        {
            foreach (var relative in DumpcapRelativeToDrivePaths)
            {
                var candidate = SafeCombine(driveRoot, relative);
                if (candidate is not null && SafeFileExists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string? FindTcpdumpPath()
    {
        var onPath = ProcessRunner.ResolveCommand("tcpdump");
        if (!string.IsNullOrEmpty(onPath) && SafeFileExists(onPath)) return onPath;
        foreach (var candidate in TcpdumpCandidates)
        {
            if (SafeFileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>读取注册表 Uninstall 中 Wireshark 的 InstallLocation（兼容 HKLM/HKCU 与 32/64 位视图）。</summary>
    private static List<string> GetWiresharkInstallLocations()
    {
        var locations = new List<string>();
        if (!OperatingSystem.IsWindows()) return locations;

        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

        foreach (var view in views)
        {
            foreach (var hive in hives)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);

                    // 部分安装只写 SOFTWARE\Wireshark\InstallDir
                    using (var wiresharkKey = baseKey.OpenSubKey(@"SOFTWARE\Wireshark"))
                    {
                        if (wiresharkKey?.GetValue("InstallDir") is string installDir &&
                            !string.IsNullOrWhiteSpace(installDir))
                        {
                            locations.Add(installDir.Trim());
                        }
                    }

                    using var uninstallKey = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallKey is null) continue;

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var appKey = uninstallKey.OpenSubKey(subKeyName);
                            if (appKey is null) continue;
                            if (appKey.GetValue("DisplayName") is not string displayName ||
                                !displayName.Contains("Wireshark", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (appKey.GetValue("InstallLocation") is string location &&
                                !string.IsNullOrWhiteSpace(location))
                            {
                                locations.Add(location.Trim());
                            }
                        }
                        catch
                        {
                            // 单个子键读取失败继续
                        }
                    }
                }
                catch
                {
                    // 注册表视图不可用时继续下一个
                }
            }
        }

        return locations
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetFixedDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.Name);
                }
                catch
                {
                    // 单个磁盘不可用时跳过
                }
            }
        }
        catch
        {
            // DriveInfo 整体不可用时返回已收集结果
        }
        return roots;
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeCombine(string root, string relative)
    {
        try
        {
            return Path.Combine(root, relative);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeCombine(string root, string relative, string fileName)
    {
        try
        {
            return Path.Combine(root, relative, fileName);
        }
        catch
        {
            return null;
        }
    }

    private async Task<CaptureInterface?> SelectInterfaceAsync(
        CaptureTool tool,
        CancellationToken cancellationToken)
    {
        if (tool.IsDumpcap)
        {
            var result = await ProcessRunner.RunAsync(tool.Path, new[] { "-D" }, 8000, cancellationToken)
                .ConfigureAwait(false);
            if (result.StandardOutput.Length > 0)
            {
                foreach (var line in result.StandardOutput.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    var match = DumpcapInterfaceRegex().Match(trimmed);
                    if (!match.Success) continue;
                    if (IsLoopbackInterface(trimmed)) continue;

                    var name = match.Groups["name"].Value.Trim();
                    var descriptionIndex = name.LastIndexOf(" (", StringComparison.Ordinal);
                    var label = name;
                    if (descriptionIndex > 0)
                    {
                        label = name[(descriptionIndex + 2)..].Trim().TrimEnd(')');
                        name = name[..descriptionIndex].Trim();
                    }
                    if (name.Length == 0) continue;

                    return new CaptureInterface(name, label.Length > 0 ? label : name);
                }
            }

            LastError = result.StandardError.Length > 0
                ? $"读取 dumpcap 网卡列表失败：{FirstLines(result.StandardError, 3)}"
                : "dumpcap 未列出可用网卡（可能缺少 Npcap 驱动）。";
            return null;
        }

        var nic = SelectNetworkInterface();
        if (nic is not null)
            return new CaptureInterface(
                nic.Name,
                string.IsNullOrWhiteSpace(nic.Description) ? nic.Name : nic.Description);

        if (PlatformInfo.IsLinux) return new CaptureInterface("any", "any");
        if (PlatformInfo.IsMacOS) return new CaptureInterface("en0", "en0");
        return null;
    }

    private static bool IsLoopbackInterface(string line) =>
        line.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("回环", StringComparison.Ordinal) ||
        line.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase);

    private static NetworkInterface? SelectNetworkInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(HasGateway)
                .ThenByDescending(nic => nic.NetworkInterfaceType is
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or
                    NetworkInterfaceType.GigabitEthernet)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                .Any(gateway => gateway.Address is not null && !gateway.Address.Equals(IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 抓包到临时文件

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private async Task<string?> CaptureToFileAsync(
        CaptureTool tool,
        string interfaceName,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var file = Path.Combine(Path.GetTempPath(), $"netcable-discovery-{Guid.NewGuid():N}.pcap");
        var arguments = tool.IsDumpcap
            ? new[] { "-i", interfaceName, "-a", $"duration:{durationSeconds}", "-P", "-w", file, "-q" }
            : new[] { "-i", interfaceName, "-s", "2000", "-U", "-w", file, "-q" };

        Process? process = null;
        try
        {
            process = new Process { StartInfo = CreateStartInfo(tool.Path, arguments) };
            if (!process.Start())
            {
                LastError = "抓包进程启动失败。";
                TryDelete(file);
                return null;
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync(CancellationToken.None);

            // dumpcap 依靠 -a duration 自行退出；tcpdump 由本服务在抓包窗口结束后结束进程。
            var limitSeconds = tool.IsDumpcap ? durationSeconds + FallbackDurationGraceSeconds : durationSeconds;
            var windowTask = Task.Delay(TimeSpan.FromSeconds(limitSeconds), cancellationToken);
            var completed = await Task.WhenAny(exitTask, windowTask).ConfigureAwait(false);
            var hitWindow = completed != exitTask;

            if (hitWindow)
            {
                TryKill(process);
                if (tool.IsDumpcap)
                {
                    LastError = $"抓包超时（超过 {limitSeconds} 秒），已结束抓包进程。";
                    await SafeWaitForExitAsync(process).ConfigureAwait(false);
                    TryDelete(file);
                    return null;
                }
            }

            await SafeWaitForExitAsync(process).ConfigureAwait(false);
            try { await exitTask.ConfigureAwait(false); } catch { /* 进程被结束，忽略 */ }

            var stderr = (await SafeReadAsync(stderrTask).ConfigureAwait(false)).Trim();
            var stdout = (await SafeReadAsync(stdoutTask).ConfigureAwait(false)).Trim();

            // 文件可能还在落盘，短暂等待其出现并有内容
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (File.Exists(file) && new FileInfo(file).Length >= 24) break;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(file))
            {
                LastError = BuildCaptureError("抓包进程未生成输出文件", stderr, stdout);
                return null;
            }

            var length = new FileInfo(file).Length;
            if (length < 24)
            {
                LastError = BuildCaptureError("抓包文件为空（可能缺少抓包权限，或抓包窗口内没有流量）", stderr, stdout);
                TryDelete(file);
                return null;
            }

            return file;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            TryDelete(file);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            LastError = $"抓包失败：{ex.Message}";
            TryDelete(file);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string BuildCaptureError(string message, string stderr, string stdout)
    {
        var detail = stderr.Length > 0 ? stderr : stdout;
        return detail.Length > 0 ? $"{message}：{FirstLines(detail, 4)}" : message;
    }

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(count);
        var joined = string.Join(" / ", lines);
        return joined.Length > 400 ? joined[..400] + "…" : joined;
    }

    // ------------------------------------------------------------------ pcap 解析

    private async Task<PcapParseResult> ParsePcapFileAsync(
        string path,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 24) return PcapParseResult.Failed("抓包文件头不完整。");

            var expected = (int)Math.Min(info.Length, MaxCaptureBytes);
            var data = new byte[expected];
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var read = 0;
            while (read < data.Length)
            {
                var count = await stream.ReadAsync(data.AsMemory(read), cancellationToken)
                    .ConfigureAwait(false);
                if (count <= 0) break;
                read += count;
            }
            if (read < data.Length) Array.Resize(ref data, read);

            return ParsePcap(data, interfaceName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PcapParseResult.Failed($"解析抓包文件失败：{ex.Message}");
        }
    }

    private static PcapParseResult ParsePcap(byte[] data, string interfaceName)
    {
        var neighbors = new List<DiscoveryNeighbor>();
        if (data.Length < 24) return PcapParseResult.Failed("pcap 文件头不完整。");

        if (!TryGetPcapFormat(data, out var bigEndian, out var linkType))
        {
            var isPcapNg = data.Length >= 4 &&
                           data[0] == 0x0A && data[1] == 0x0D && data[2] == 0x0D && data[3] == 0x0A;
            return PcapParseResult.Failed(isPcapNg
                ? "抓包文件是 pcapng 格式，当前仅支持经典 pcap（dumpcap 需使用 -P）。"
                : "无法识别的抓包文件格式（不是经典 pcap）。");
        }

        var offset = 24;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var frameCount = 0;
        var lldpFrames = 0;
        var cdpFrames = 0;

        while (offset + 16 <= data.Length)
        {
            var includedLength = (int)ReadUInt32(data, offset + 8, bigEndian);
            offset += 16;
            if (includedLength <= 0 || includedLength > MaxPacketLength ||
                includedLength > data.Length - offset)
            {
                break; // 截断的尾包/异常记录：停止解析，保留已解析结果
            }

            var packet = data.AsSpan(offset, includedLength);
            offset += includedLength;
            frameCount++;

            try
            {
                var neighbor = ParsePacket(packet, linkType, interfaceName);
                if (neighbor is null) continue;
                if (neighbor.Protocol == "LLDP") lldpFrames++;
                else if (neighbor.Protocol == "CDP") cdpFrames++;
                MergeNeighbor(neighbors, seen, neighbor);
            }
            catch
            {
                // 单包格式异常：跳过，不影响其它包
            }
        }

        return new PcapParseResult(neighbors, frameCount, lldpFrames, cdpFrames, null);
    }

    private static bool TryGetPcapFormat(byte[] data, out bool bigEndian, out uint linkType)
    {
        bigEndian = false;
        linkType = 0;
        var magic = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
        switch (magic)
        {
            case 0xA1B2C3D4: // 大端，微秒
                bigEndian = true;
                break;
            case 0xD4C3B2A1: // 小端，微秒
                bigEndian = false;
                break;
            case 0xA1B23C4D: // 大端，纳秒
                bigEndian = true;
                break;
            case 0x4D3CB2A1: // 小端，纳秒
                bigEndian = false;
                break;
            default:
                return false;
        }

        linkType = ReadUInt32(data, 20, bigEndian);
        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static void MergeNeighbor(
        List<DiscoveryNeighbor> neighbors,
        Dictionary<string, int> seen,
        DiscoveryNeighbor neighbor)
    {
        var key = $"{neighbor.Protocol}\u0000{neighbor.ChassisId}\u0000{neighbor.PortId}";
        if (!seen.TryGetValue(key, out var index))
        {
            seen[key] = neighbors.Count;
            neighbors.Add(neighbor);
            return;
        }

        // 同一邻居的后续帧可能补齐之前缺失的字段
        var existing = neighbors[index];
        neighbors[index] = existing with
        {
            ChassisId = existing.ChassisId ?? neighbor.ChassisId,
            PortId = existing.PortId ?? neighbor.PortId,
            SystemName = existing.SystemName ?? neighbor.SystemName,
            ManagementAddress = existing.ManagementAddress ?? neighbor.ManagementAddress,
            Ttl = existing.Ttl ?? neighbor.Ttl
        };
    }

    /// <summary>解析单个链路层帧（以太网 / Linux SLL），供 pcap 与 Npcap 直连共用。</summary>
    internal static DiscoveryNeighbor? ParsePacket(
        ReadOnlySpan<byte> packet,
        uint linkType,
        string interfaceName) => linkType switch
    {
        LinkTypeEthernet => ParseEthernet(packet, interfaceName),
        LinkTypeLinuxSll => ParseLinuxSll(packet, interfaceName),
        LinkTypeLinuxSll2 => ParseLinuxSll2(packet, interfaceName),
        _ => null
    };

    private static DiscoveryNeighbor? ParseEthernet(ReadOnlySpan<byte> packet, string interfaceName)
    {
        if (packet.Length < 14) return null;

        var destination = packet[..6];
        var offset = 12;
        var etherType = ReadUInt16(packet, offset);
        offset = 14;

        // 跳过最多两层 VLAN 标签（0x8100 / 0x88A8），使链路聚合/Trunk 口的帧也能识别
        for (var depth = 0; depth < 2 && (etherType == 0x8100 || etherType == 0x88A8); depth++)
        {
            if (offset + 4 > packet.Length) return null;
            etherType = ReadUInt16(packet, offset + 2);
            offset += 4;
        }

        if (etherType == 0x88CC) return ParseLldp(packet[offset..], interfaceName);
        if (IsCdpDestination(destination)) return ParseCdp(packet, offset, interfaceName);
        return null;
    }

    private static DiscoveryNeighbor? ParseLinuxSll(ReadOnlySpan<byte> packet, string interfaceName)
    {
        if (packet.Length < 16) return null;
        var etherType = ReadUInt16(packet, 14);
        var offset = 16;
        for (var depth = 0; depth < 2 && (etherType == 0x8100 || etherType == 0x88A8); depth++)
        {
            if (offset + 4 > packet.Length) return null;
            etherType = ReadUInt16(packet, offset + 2);
            offset += 4;
        }

        if (etherType == 0x88CC) return ParseLldp(packet[offset..], interfaceName);
        if (etherType <= 1500) return ParseCdp(packet, offset, interfaceName);
        return null;
    }

    private static DiscoveryNeighbor? ParseLinuxSll2(ReadOnlySpan<byte> packet, string interfaceName)
    {
        if (packet.Length < 20) return null;
        var etherType = ReadUInt16(packet, 0);
        var offset = 20;
        for (var depth = 0; depth < 2 && (etherType == 0x8100 || etherType == 0x88A8); depth++)
        {
            if (offset + 4 > packet.Length) return null;
            etherType = ReadUInt16(packet, offset + 2);
            offset += 4;
        }

        if (etherType == 0x88CC) return ParseLldp(packet[offset..], interfaceName);
        if (etherType <= 1500) return ParseCdp(packet, offset, interfaceName);
        return null;
    }

    private static bool IsCdpDestination(ReadOnlySpan<byte> destination) =>
        destination.Length == 6 &&
        destination[0] == 0x01 && destination[1] == 0x00 && destination[2] == 0x0C &&
        destination[3] == 0xCC && destination[4] == 0xCC && destination[5] == 0xCC;

    // ------------------------------------------------------------------ LLDP

    private static DiscoveryNeighbor? ParseLldp(ReadOnlySpan<byte> lldp, string interfaceName)
    {
        string? chassisId = null;
        string? portId = null;
        string? systemName = null;
        string? managementAddress = null;
        string? ttl = null;

        var offset = 0;
        while (offset + 2 <= lldp.Length)
        {
            var header = (lldp[offset] << 8) | lldp[offset + 1];
            var type = header >> 9;
            var length = header & 0x1FF;
            offset += 2;

            if (type == 0) break; // End of LLDPDU
            if (length > lldp.Length - offset) break;

            var value = lldp.Slice(offset, length);
            switch (type)
            {
                case 1: chassisId = DecodeLldpId(value, 4, 5); break;    // Chassis ID：4=MAC，5=网络地址
                case 2: portId = DecodeLldpId(value, 3, 4); break;       // Port ID：3=MAC，4=网络地址
                case 3:                                                   // Time To Live
                    if (length >= 2) ttl = ReadUInt16(value, 0).ToString(CultureInfo.InvariantCulture);
                    break;
                case 5: systemName = DecodeText(value); break;            // System Name
                case 8: managementAddress = DecodeLldpManagementAddress(value); break; // Management Address
            }

            offset += length;
        }

        if (chassisId is null && portId is null && systemName is null && managementAddress is null) return null;
        return new DiscoveryNeighbor(interfaceName, "LLDP", chassisId, portId, systemName, managementAddress, ttl);
    }

    /// <summary>
    /// 解码 LLDP 标识 TLV。Chassis ID 与 Port ID 的 subtype 编号含义不同
    /// （Chassis：4=MAC、5=网络地址；Port：3=MAC、4=网络地址），因此由调用方传入。
    /// </summary>
    private static string? DecodeLldpId(ReadOnlySpan<byte> value, int macSubtype, int networkSubtype)
    {
        if (value.Length < 1) return null;
        var subtype = value[0];
        var body = value[1..];
        if (subtype == macSubtype) return FormatMac(body);
        if (subtype == networkSubtype) return DecodeNetworkAddress(body);
        return DecodeText(body) ?? FormatHex(body);
    }

    private static string? DecodeLldpManagementAddress(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2) return null;
        var addressLength = value[0];
        if (addressLength < 1 || addressLength + 1 > value.Length) return null;

        var subtype = value[1];
        var address = value.Slice(2, addressLength - 1);
        if (subtype == 1 && address.Length == 4) return FormatIp(address);
        if (subtype == 2 && address.Length == 16) return FormatIp(address);
        return address.Length == 0 ? null : FormatHex(address);
    }

    private static string? DecodeNetworkAddress(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return null;
        var family = body[0];
        var address = body[1..];
        if (family == 1 && address.Length == 4) return FormatIp(address);
        if (family == 2 && address.Length == 16) return FormatIp(address);
        return FormatHex(address);
    }

    private static string? FormatIp(ReadOnlySpan<byte> address)
    {
        try
        {
            return new IPAddress(address.ToArray()).ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatMac(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6) return body.Length == 0 ? null : FormatHex(body);
        return string.Join(':', body.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string? FormatHex(ReadOnlySpan<byte> body) =>
        body.Length == 0 ? null : Convert.ToHexString(body);

    private static string? DecodeText(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0) return null;
        foreach (var item in value)
        {
            if (item < 0x20 || item > 0x7E) return null;
        }
        return Encoding.ASCII.GetString(value);
    }

    // ------------------------------------------------------------------ CDP

    private static DiscoveryNeighbor? ParseCdp(ReadOnlySpan<byte> packet, int llcOffset, string interfaceName)
    {
        // 802.3 帧：LLC(SAP=0xAA/0xAA, Control=0x03) + SNAP(OUI) + PID=0x2000
        if (llcOffset + 8 > packet.Length) return null;
        if (packet[llcOffset] != 0xAA || packet[llcOffset + 1] != 0xAA || packet[llcOffset + 2] != 0x03)
            return null;
        if (ReadUInt16(packet, llcOffset + 6) != 0x2000) return null;

        var offset = llcOffset + 8;
        string? deviceId = null;
        string? portId = null;
        string? managementAddress = null;

        while (offset + 4 <= packet.Length)
        {
            var type = ReadUInt16(packet, offset);
            var totalLength = ReadUInt16(packet, offset + 2);
            if (totalLength < 4) break;

            var valueLength = totalLength - 4;
            offset += 4;
            if (valueLength > packet.Length - offset) break;

            var value = packet.Slice(offset, valueLength);
            switch (type)
            {
                case 1: deviceId = DecodeText(value); break;                     // Device ID
                case 2: managementAddress ??= DecodeCdpAddresses(value); break;  // Addresses
                case 3: portId = DecodeText(value); break;                       // Port ID
            }

            offset += valueLength;
        }

        if (deviceId is null && portId is null && managementAddress is null) return null;
        return new DiscoveryNeighbor(interfaceName, "CDP", deviceId, portId, deviceId, managementAddress, null);
    }

    private static string? DecodeCdpAddresses(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(value[..4]);
        if (count > 64) count = 64; // 防御异常数据

        var offset = 4;
        for (var index = 0u; index < count; index++)
        {
            if (offset + 2 > value.Length) break;
            var protocolType = value[offset];
            var protocolLength = value[offset + 1];
            offset += 2;
            if (protocolLength > value.Length - offset) break;

            var protocol = value.Slice(offset, protocolLength);
            offset += protocolLength;
            if (offset + 2 > value.Length) break;

            var addressLength = ReadUInt16(value, offset);
            offset += 2;
            if (addressLength > value.Length - offset) break;

            var address = value.Slice(offset, addressLength);
            offset += addressLength;

            // NLPID：0xCC = IPv4，0x8E = IPv6
            if (protocolLength == 1 && protocol[0] == 0xCC && address.Length == 4)
                return FormatIp(address);
            if (protocolLength == 1 && protocol[0] == 0x8E && address.Length == 16)
                return FormatIp(address);
        }

        return null;
    }

    // ------------------------------------------------------------------ 进程/文件小工具

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task SafeWaitForExitAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(3000);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 进程可能已被结束或无法等待
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 尽量结束，失败忽略
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件删除失败不影响结果
        }
    }

    /// <summary>抓包工具描述。IsDumpcap 决定抓包参数与网卡枚举方式。</summary>
    private readonly record struct CaptureTool(string Path, bool IsDumpcap)
    {
        public string KindName => IsDumpcap ? "dumpcap（Wireshark）" : "tcpdump";
        public string BackendName => IsDumpcap ? "Wireshark dumpcap" : "tcpdump";
    }

    /// <summary>抓包网卡：Name 传给抓包工具，Label 用于结果展示。</summary>
    private readonly record struct CaptureInterface(string Name, string Label);

    /// <summary>pcap 解析结果：邻居列表 + 帧统计。</summary>
    private readonly record struct PcapParseResult(
        List<DiscoveryNeighbor> Neighbors,
        int FrameCount,
        int LldpCount,
        int CdpCount,
        string? Error)
    {
        public static PcapParseResult Failed(string error) =>
            new(new List<DiscoveryNeighbor>(), 0, 0, 0, error);
    }
}

