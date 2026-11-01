using NetCable.Core.Models;

namespace NetCable.Core.Contracts;

public interface ISnmpService
{
    Task<SnmpResult> GetAsync(SnmpRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SnmpVarBind> WalkAsync(SnmpRequest request, CancellationToken cancellationToken = default);
}

public interface IHostsFileService
{
    Task<HostsFileInfo> GetInfoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HostsEntry>> ReadAsync(CancellationToken cancellationToken = default);
    Task<string> BackupAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(IReadOnlyList<HostsEntry> entries, CancellationToken cancellationToken = default);
}

public interface IRemoteClientService
{
    Task<IReadOnlyList<RemoteClientInfo>> GetClientsAsync(CancellationToken cancellationToken = default);
    /// <summary>返回将要执行的命令行（不执行）。</summary>
    Task<string> BuildCommandAsync(string clientId, string target, CancellationToken cancellationToken = default);
    /// <summary>启动系统原生客户端；返回说明文本。</summary>
    Task<string> LaunchAsync(string clientId, string target, CancellationToken cancellationToken = default);
}

public interface IFirewallService
{
    Task<FirewallStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirewallRule>> GetRulesAsync(CancellationToken cancellationToken = default);
}

public interface IWifiBssService
{
    /// <summary>检测当前进程能否读取 AP 列表；不能时给出原因与是否需要提权。</summary>
    Task<WifiAccessState> GetAccessStateAsync(CancellationToken cancellationToken = default);

    /// <summary>读取周边 AP 详情（含 beacon/probe-response 的 IE 解析）。</summary>
    Task<IReadOnlyList<WifiBssDetail>> GetBssListAsync(CancellationToken cancellationToken = default);
}

public interface IWifiMonitorService
{
    /// <summary>检测 802.11 monitor mode 可用性（需要网卡驱动支持）。</summary>
    Task<WifiMonitorCapability> GetCapabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>以 monitor mode 捕获 802.11 管理帧。</summary>
    IAsyncEnumerable<WifiFrameInfo> CaptureAsync(int durationSeconds, CancellationToken cancellationToken = default);
}

public interface IDiscoveryProtocolService
{
    Task<(bool Supported, string? Reason)> GetCapabilityAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<DiscoveryNeighbor> CaptureAsync(int durationSeconds, CancellationToken cancellationToken = default);
}
