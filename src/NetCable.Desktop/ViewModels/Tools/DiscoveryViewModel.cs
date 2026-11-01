using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetCable.Core.Models;
using NetCable.Services.Network;

namespace NetCable.Desktop.ViewModels.Tools;

/// <summary>
/// Discovery Protocol：被动监听 LLDP/CDP 邻居通告。
/// 抓包后端优先级：Npcap 直连 → Wireshark dumpcap → tcpdump → Windows 内置 pktmon。
/// </summary>
[ToolPage("discovery")]
public sealed partial class DiscoveryViewModel : ToolViewModelBase
{
    // 用具体类型而非接口：需要读取 LastBackend / LastCaptureStats 这类后端诊断信息
    private readonly DiscoveryProtocolService _service = new();

    public DiscoveryViewModel() : base("discovery")
    {
        Durations = new ObservableCollection<string> { "10 秒", "30 秒", "60 秒" };
        SelectedDuration = "10 秒";
        Summary = "被动监听 LLDP / CDP 邻居通告（自动选择抓包后端，不需要 Wireshark）";
    }

    public ObservableCollection<string> Durations { get; }

    [ObservableProperty] private string _selectedDuration = "10 秒";
    [ObservableProperty] private string _summary = "—";

    // 不支持抓包时结果表展示安装指引，表头随之切换，避免出现 ChassisId 等无关列名。
    [ObservableProperty] private string _keyHeader = "接口";
    [ObservableProperty] private string _valueHeader = "协议";
    [ObservableProperty] private string _extraHeader = "ChassisId · PortId · 系统名 · 管理地址 · TTL";

    private int DurationSeconds
    {
        get
        {
            var text = SelectedDuration.Split(' ')[0];
            return int.TryParse(text, out var seconds) ? Math.Clamp(seconds, 1, 300) : 10;
        }
    }

    [RelayCommand]
    private void StartCapture()
    {
        if (IsRunning) return;
        RunCommand.Execute(null);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ClearOutput();
        Summary = "检测抓包能力…";
        LogInfo("检查 LLDP/CDP 抓包能力（Npcap 直连 / dumpcap / tcpdump / pktmon）…");

        bool supported;
        string? reason;
        try
        {
            (supported, reason) = await _service.GetCapabilityAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            supported = false;
            reason = "抓包能力检测失败：" + ex.Message;
        }

        var backend = _service.LastBackend;

        if (!supported)
        {
            KeyHeader = "项目";
            ValueHeader = "状态";
            ExtraHeader = "说明 / 安装指引";
            Summary = "当前环境不支持抓包：" + (reason ?? "未找到可用的抓包后端");
            LogWarn(Summary);
            AddRow("能力检测", "不可用", reason ?? "未找到抓包工具");
            AddRow("Windows（推荐）", "安装 Npcap 即可", "只需驱动，无需 Wireshark：https://npcap.com/  （勾选 WinPcap API 兼容）");
            AddRow("Windows（备选）", "Wireshark 自带 dumpcap", "装 Wireshark 后本工具会自动找到它（任意磁盘目录均可）");
            AddRow("Windows（零安装）", "内置 pktmon（需管理员）", "Windows 10 1809+ 自带，以管理员运行 NetCable 即可");
            AddRow("macOS", "系统自带 tcpdump", "需要在「系统设置 → 隐私与安全性」授权，或使用 sudo 启动");
            AddRow("Linux", "安装 tcpdump", "Debian/Ubuntu: sudo apt install tcpdump；Fedora: sudo dnf install tcpdump");
            AddRow("工作原理", "被动监听 LLDP (0x88CC) / CDP (SNAP 0x2000)", "需要网络中存在发送通告的交换机 / AP");
            AddRow("提示", "没有可用后端是环境问题，不是页面故障", "安装任一后端后重启 NetCable 即可使用");
            return;
        }

        LogOk($"抓包能力可用，后端：{backend ?? "未知"}");
        KeyHeader = "接口";
        ValueHeader = "协议";
        ExtraHeader = "ChassisId · PortId · 系统名 · 管理地址 · TTL";
        Summary = $"监听 {DurationSeconds} 秒…（后端：{backend ?? "未知"}）";

        var count = 0;
        var lldp = 0;
        var cdp = 0;

        try
        {
            await foreach (var neighbor in _service.CaptureAsync(DurationSeconds, cancellationToken).ConfigureAwait(true))
            {
                count++;
                if (neighbor.Protocol.Contains("LLDP", StringComparison.OrdinalIgnoreCase)) lldp++;
                else if (neighbor.Protocol.Contains("CDP", StringComparison.OrdinalIgnoreCase)) cdp++;

                var extra = new List<string>();
                if (!string.IsNullOrWhiteSpace(neighbor.ChassisId)) extra.Add($"ChassisId {neighbor.ChassisId}");
                if (!string.IsNullOrWhiteSpace(neighbor.PortId)) extra.Add($"PortId {neighbor.PortId}");
                if (!string.IsNullOrWhiteSpace(neighbor.SystemName)) extra.Add($"系统名 {neighbor.SystemName}");
                if (!string.IsNullOrWhiteSpace(neighbor.ManagementAddress)) extra.Add($"管理地址 {neighbor.ManagementAddress}");
                if (!string.IsNullOrWhiteSpace(neighbor.Ttl)) extra.Add($"TTL {neighbor.Ttl}");

                AddRow(neighbor.Interface, neighbor.Protocol, string.Join(" · ", extra));
                LogOk($"{neighbor.Interface} {neighbor.Protocol} · " + string.Join(" · ", extra));
                StatusText = $"已捕获 {count} 条邻居通告…";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Summary = $"监听中断（已捕获 {count} 条）：{ex.Message}";
            LogError(Summary);
            return;
        }

        var stats = _service.LastCaptureStats;
        if (!string.IsNullOrWhiteSpace(stats)) LogMuted(stats!);

        if (count == 0)
        {
            Summary = $"监听 {DurationSeconds} 秒，未捕获到 LLDP/CDP 通告（后端：{backend ?? "未知"}）";
            LogWarn("抓包链路正常，但网段内没有发送 LLDP/CDP 的交换机/AP（家用路由器通常不发，属正常现象）。");
        }
        else
        {
            Summary = $"捕获 {count} 条邻居通告（LLDP {lldp} · CDP {cdp}，后端：{backend ?? "未知"}）";
            LogOk(Summary);
        }
    }
}
