using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Models;
using NetCable.Desktop.Services;
using NetCable.Services.Network;
using NetCable.Services.Platform;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>周边 AP 的一行（系统命令扫描结果）。</summary>
public sealed class WifiApRow
{
    public WifiApRow(WifiNetworkInfo info)
    {
        Ssid = string.IsNullOrWhiteSpace(info.Ssid) ? "（隐藏网络）" : info.Ssid;
        Bssid = info.Bssid ?? "—";
        Signal = info.SignalPercent is { } signal ? $"{signal}%" : "—";
        Channel = info.Channel?.ToString() ?? "—";
        Band = info.Band ?? "—";
        Security = info.Security ?? "—";
        Rate = info.Rate ?? "—";
        IsConnected = info.IsConnected;
    }

    public string Ssid { get; }
    public string Bssid { get; }
    public string Signal { get; }
    public string Channel { get; }
    public string Band { get; }
    public string Security { get; }
    public string Rate { get; }
    public bool IsConnected { get; }
    public string StateText => IsConnected ? "已连接" : string.Empty;
}

/// <summary>AP 详情（WLAN API + beacon/probe-response IE 解析）的一行。</summary>
public sealed class WifiBssRow
{
    public WifiBssRow(WifiBssDetail detail)
    {
        Ssid = string.IsNullOrWhiteSpace(detail.Ssid) ? "（隐藏）" : detail.Ssid;
        Bssid = detail.Bssid;
        Channel = detail.Channel.ToString();
        Band = FormatBand(detail.FrequencyMhz);
        Rssi = $"{detail.RssiDbm} dBm";
        BeaconInterval = $"{detail.BeaconInterval} TU";
        Security = detail.Security;
        Cipher = detail.Cipher;
        Akm = detail.Akm;
        PhyType = detail.PhyType;
        Vendor = string.IsNullOrWhiteSpace(detail.Vendor) ? "—" : detail.Vendor;
        IeRows = detail.IeSummary
            .Select(pair => new ResultRow(pair.Key, pair.Value))
            .ToList();
    }

    public string Ssid { get; }
    public string Bssid { get; }
    public string Channel { get; }
    public string Band { get; }
    public string Rssi { get; }
    public string BeaconInterval { get; }
    public string Security { get; }
    public string Cipher { get; }
    public string Akm { get; }
    public string PhyType { get; }
    public string Vendor { get; }
    public IReadOnlyList<ResultRow> IeRows { get; }

    private static string FormatBand(int frequencyMhz) => frequencyMhz switch
    {
        <= 0 => "—",
        < 2500 => "2.4 GHz",
        < 5925 => "5 GHz",
        _ => "6 GHz"
    };
}

/// <summary>802.11 管理帧的一行。</summary>
public sealed class WifiFrameRow
{
    public WifiFrameRow(WifiFrameInfo frame)
    {
        FrameType = frame.FrameType;
        SubType = frame.SubType;
        Source = frame.Source;
        Destination = frame.Destination;
        Bssid = frame.Bssid;
        Ssid = frame.Ssid ?? "—";
        Channel = frame.Channel?.ToString() ?? "—";
        Rssi = frame.RssiDbm is { } rssi ? $"{rssi} dBm" : "—";
        Sequence = frame.Sequence?.ToString() ?? "—";
        Detail = frame.Detail ?? string.Empty;
    }

    public string FrameType { get; }
    public string SubType { get; }
    public string Source { get; }
    public string Destination { get; }
    public string Bssid { get; }
    public string Ssid { get; }
    public string Channel { get; }
    public string Rssi { get; }
    public string Sequence { get; }
    public string Detail { get; }
}

/// <summary>
/// WiFi：当前连接 + 周边 AP（系统命令）；802.11 管理帧 IE 详情与 monitor mode 抓帧（需提权，弹窗授权）。
/// </summary>
[ToolPage("wifi")]
public sealed partial class WifiViewModel : ToolViewModelBase
{
    private readonly WifiService _service = new();
    private readonly WifiBssService _bssService = new();
    private readonly WifiMonitorService _monitorService = new();

    private CancellationTokenSource? _taskCts;

    public WifiViewModel() : base("wifi") { }

    public ObservableCollection<WifiApRow> AccessPoints { get; } = new();
    public ObservableCollection<WifiBssRow> BssRows { get; } = new();
    public ObservableCollection<WifiFrameRow> Frames { get; } = new();

    public IReadOnlyList<int> MonitorDurations { get; } = new[] { 10, 20, 30 };

    [ObservableProperty] private string _summary = "—";
    [ObservableProperty] private string _notice = string.Empty;

    // ---- 区块 A：AP 详情（802.11 管理帧 IE，需管理员授权）----
    [ObservableProperty] private string _bssStateText = "尚未检测";
    [ObservableProperty] private string _bssNotice = string.Empty;
    [ObservableProperty] private bool _canRequestElevation;
    [ObservableProperty] private bool _hasBssRows;
    [ObservableProperty] private WifiBssRow? _selectedBss;
    [ObservableProperty] private bool _hasSelectedBss;

    // ---- 区块 B：802.11 管理帧捕获（monitor mode）----
    [ObservableProperty] private string _monitorStateText = "尚未检测";
    [ObservableProperty] private string _monitorReason = string.Empty;
    [ObservableProperty] private string _monitorNotice = string.Empty;
    [ObservableProperty] private bool _monitorSupported;
    [ObservableProperty] private int _monitorSeconds = 10;
    [ObservableProperty] private bool _hasFrames;

    // ---- 提权任务运行状态 ----
    [ObservableProperty] private bool _isElevatedRunning;

    private static Window? OwnerWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    partial void OnSelectedBssChanged(WifiBssRow? value) => HasSelectedBss = value is not null;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        AccessPoints.Clear();
        BssRows.Clear();
        Frames.Clear();
        SelectedBss = null;
        HasBssRows = false;
        HasFrames = false;
        Summary = "—";
        Notice = string.Empty;
        BssNotice = string.Empty;
        MonitorNotice = string.Empty;

        await RefreshSystemScanAsync(cancellationToken).ConfigureAwait(true);
        await RefreshCapabilitiesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>原有区块：当前连接 + 周边 AP（系统命令，不需要管理员）。</summary>
    private async Task RefreshSystemScanAsync(CancellationToken cancellationToken)
    {
        if (!_service.IsSupported)
        {
            Notice = $"当前平台不支持 WiFi 扫描：{_service.UnsupportedReason ?? "原因未知"}";
            AddRow("WiFi 状态", "不可用", _service.UnsupportedReason ?? "原因未知");
            LogWarn(Notice);
        }
        else
        {
            LogInfo("读取当前 WiFi 连接…");
            var current = await _service.GetCurrentAsync(cancellationToken).ConfigureAwait(true);
            if (current is null)
            {
                AddRow("当前连接", "未连接到 WiFi（可能使用有线网络）");
                LogWarn("未检测到当前 WiFi 连接");
            }
            else
            {
                AddRow("SSID", current.Ssid);
                AddRow("BSSID", current.Bssid ?? "—");
                AddRow("信号", current.SignalPercent is { } signal ? $"{signal}%" : "—");
                AddRow("信道", current.Channel?.ToString() ?? "—");
                AddRow("频段", current.Band ?? "—");
                AddRow("加密", current.Security ?? "—");
                AddRow("速率", current.Rate ?? "—");
                LogOk($"当前网络：{current.Ssid}");
            }

            LogInfo("扫描周边 AP…");
            var aps = await _service.ScanAsync(cancellationToken).ConfigureAwait(true);
            foreach (var ap in aps.OrderByDescending(a => a.SignalPercent ?? -1).ThenBy(a => a.Ssid))
                AccessPoints.Add(new WifiApRow(ap));

            Summary = current is null
                ? $"未连接 WiFi · 周边 {aps.Count} 个 AP"
                : $"当前 {current.Ssid} · 信号 {current.SignalPercent?.ToString() ?? "—"}% · 周边 {aps.Count} 个 AP";
            LogOk($"扫描完成：{aps.Count} 个周边 AP（按信号强度排序）");
        }
    }

    /// <summary>检测两个新增区块的权限/能力状态（不触发任何提权）。</summary>
    private async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        // 区块 A：WLAN AP 列表读取权限
        try
        {
            var access = await _bssService.GetAccessStateAsync(cancellationToken).ConfigureAwait(true);
            CanRequestElevation = !access.CanReadBssList && access.RequiresElevation;

            if (access.CanReadBssList)
            {
                BssStateText = "当前进程可直接读取 AP 详情";
                BssNotice = access.Hint ?? "无需提权，可直接读取 WLAN AP 详情。";
                LogOk("WLAN AP 详情：当前权限可读");
                await LoadBssDirectAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                BssStateText = access.RequiresElevation
                    ? "需要管理员授权（WLAN API 返回错误 5 ACCESS_DENIED）"
                    : "当前环境无法读取 AP 详情";

                BssNotice = access.BlockReason
                            ?? "非管理员读取 WLAN API 的 BSS/IE 信息会被拒绝（Windows 11 24H2 起必须提权）。";

                if (access.RequiresElevation)
                    BssNotice += " 点击「以管理员权限读取」将触发 Windows UAC 授权。";

                LogWarn($"WLAN AP 详情：{BssNotice}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            BssStateText = "权限检测失败";
            BssNotice = $"读取 WLAN 权限状态失败：{ex.Message}";
            CanRequestElevation = false;
            LogError(BssNotice);
        }

        // 区块 B：monitor mode 能力
        try
        {
            var capability = await _monitorService.GetCapabilityAsync(cancellationToken).ConfigureAwait(true);
            MonitorSupported = capability.Supported;
            MonitorReason = capability.Reason ?? string.Empty;
            MonitorStateText = capability.Supported
                ? $"支持 monitor mode{(string.IsNullOrWhiteSpace(capability.AdapterDescription) ? string.Empty : $" · {capability.AdapterDescription}")}"
                : $"不支持 monitor mode：{capability.Reason ?? "原因未知"}";
            MonitorNotice = capability.Supported
                ? "以 monitor mode 捕获原始 802.11 管理帧需要管理员权限，且 Npcap 安装时需勾选 raw 802.11 支持。"
                : "需要支持 monitor mode 的外置 USB 网卡，并在安装 Npcap 时勾选 raw 802.11；本机 Intel 无线网卡 驱动不支持 monitor mode，即使提权也无法抓帧。";

            if (capability.Supported)
                LogOk(MonitorStateText);
            else
                LogWarn(MonitorStateText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MonitorSupported = false;
            MonitorStateText = "能力检测失败";
            MonitorReason = ex.Message;
            MonitorNotice = ex.Message;
            LogError($"monitor mode 能力检测失败：{ex.Message}");
        }
    }

    /// <summary>当前进程已有权限时直接读取 AP 详情（不弹窗、不提权）。</summary>
    private async Task LoadBssDirectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var items = await _bssService.GetBssListAsync(cancellationToken).ConfigureAwait(true);
            FillBssRows(items);
            LogOk($"读取到 {items.Count} 个 AP 详情（当前权限）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            BssNotice = $"读取 AP 详情失败：{ex.Message}";
            LogError(BssNotice);
        }
    }

    /// <summary>区块 A：确认后用管理员权限读取 AP 详情（触发 UAC）。</summary>
    [RelayCommand]
    private async Task ReadBssElevatedAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync(OwnerWindow,
            "需要管理员权限",
            "读取 WLAN AP 详情（beacon / probe-response 的 802.11 信息元素）需要管理员权限。\n\n" +
            "Windows 11 24H2 允许非管理员枚举 SSID，但通过 WLAN API 读取 BSS/IE 时会返回错误 5（ACCESS_DENIED）。\n\n" +
            "继续后 NetCable 会以管理员身份启动一个临时实例执行查询，将触发 Windows UAC 授权。是否继续？").ConfigureAwait(true);

        if (!confirmed)
        {
            BssNotice = "已取消，未获得管理员权限";
            LogWarn(BssNotice);
            return;
        }

        _taskCts?.Dispose();
        _taskCts = new CancellationTokenSource();
        IsElevatedRunning = true;
        BssNotice = "正在等待 UAC 授权并读取 AP 详情…";
        StatusText = "提权读取中…";
        try
        {
            var (items, error) = await ElevatedBridge.RunWifiBssAsync(_taskCts.Token).ConfigureAwait(true);
            if (items is null)
            {
                BssNotice = error ?? "未返回结果";
                LogWarn($"AP 详情读取失败：{BssNotice}");
                StatusText = "读取失败";
            }
            else
            {
                FillBssRows(items);
                BssStateText = $"已通过管理员权限读取（{items.Count} 个 AP）";
                BssNotice = $"成功读取 {items.Count} 个 AP 的 802.11 管理帧信息元素。选中某一行可在下方查看 IE 详情。";
                LogOk(BssNotice);
                StatusText = "完成";
            }
        }
        catch (OperationCanceledException)
        {
            BssNotice = "已取消提权读取";
            LogMuted(BssNotice);
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            BssNotice = $"提权读取失败：{ex.Message}";
            LogError(BssNotice);
            StatusText = "失败";
        }
        finally
        {
            IsElevatedRunning = false;
            _taskCts?.Dispose();
            _taskCts = null;
        }
    }

    /// <summary>区块 B：确认后会以管理员权限以 monitor mode 捕获管理帧（触发 UAC）。</summary>
    [RelayCommand]
    private async Task StartMonitorAsync()
    {
        if (!MonitorSupported)
        {
            MonitorNotice = $"当前网卡/驱动不支持 monitor mode：{MonitorReason}";
            LogWarn(MonitorNotice);
            return;
        }

        var seconds = Math.Clamp(MonitorSeconds, 3, 120);
        var confirmed = await Dialogs.ConfirmAsync(OwnerWindow,
            "开始 802.11 管理帧捕获",
            $"将在管理员权限下以 monitor mode 捕获 {seconds} 秒 802.11 管理帧。\n\n" +
            "这需要支持 monitor mode 的无线网卡，并在安装 Npcap 时勾选 raw 802.11 支持；" +
            "NetCable 会以管理员身份启动临时实例执行捕获，将触发 Windows UAC 授权。是否继续？").ConfigureAwait(true);

        if (!confirmed)
        {
            MonitorNotice = "已取消，未获得管理员权限";
            LogWarn(MonitorNotice);
            return;
        }

        _taskCts?.Dispose();
        _taskCts = new CancellationTokenSource();
        IsElevatedRunning = true;
        Frames.Clear();
        HasFrames = false;
        MonitorNotice = $"正在等待授权并捕获 {seconds} 秒管理帧…";
        StatusText = "捕获中…";
        try
        {
            var (frames, error) = await ElevatedBridge.RunWifiMonitorAsync(seconds, _taskCts.Token).ConfigureAwait(true);
            if (frames is null)
            {
                MonitorNotice = error ?? "未返回结果";
                LogWarn($"管理帧捕获失败：{MonitorNotice}");
                StatusText = "捕获失败";
            }
            else
            {
                foreach (var frame in frames)
                    Frames.Add(new WifiFrameRow(frame));

                HasFrames = Frames.Count > 0;
                MonitorNotice = Frames.Count == 0
                    ? "捕获完成，但没有抓到管理帧（可能网卡不支持 monitor mode 或该时段无流量）。"
                    : $"捕获完成：{Frames.Count} 帧 802.11 管理帧。";
                LogOk(MonitorNotice);
                StatusText = "完成";
            }
        }
        catch (OperationCanceledException)
        {
            MonitorNotice = "已取消管理帧捕获";
            LogMuted(MonitorNotice);
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            MonitorNotice = $"管理帧捕获失败：{ex.Message}";
            LogError(MonitorNotice);
            StatusText = "失败";
        }
        finally
        {
            IsElevatedRunning = false;
            _taskCts?.Dispose();
            _taskCts = null;
        }
    }

    /// <summary>停止提权任务与普通刷新任务。</summary>
    [RelayCommand]
    private void StopAll()
    {
        _taskCts?.Cancel();
        CancelCommand.Execute(null);
        StatusText = "正在停止…";
    }

    private void FillBssRows(IReadOnlyList<WifiBssDetail> items)
    {
        BssRows.Clear();
        foreach (var item in items.OrderByDescending(d => d.RssiDbm))
            BssRows.Add(new WifiBssRow(item));

        HasBssRows = BssRows.Count > 0;
        SelectedBss = BssRows.FirstOrDefault();
    }
}
