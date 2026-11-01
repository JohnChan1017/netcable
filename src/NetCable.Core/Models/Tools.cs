namespace NetCable.Core.Models;

// ---------- SNMP ----------
public sealed record SnmpRequest
{
    public required string Host { get; init; }
    public string Community { get; init; } = "public";
    public int Version { get; init; } = 2;          // 1 = v1, 2 = v2c
    public string Oid { get; init; } = "1.3.6.1.2.1.1.1.0";
    public int TimeoutMs { get; init; } = 3000;
    public int MaxRows { get; init; } = 200;
}

public sealed record SnmpVarBind(string Oid, string Type, string Value);

public sealed record SnmpResult(
    bool Success,
    string Message,
    IReadOnlyList<SnmpVarBind> Variables);

// ---------- Hosts 文件 ----------
public sealed record HostsEntry(string IpAddress, string HostName, string? Comment, int LineNumber)
{
    public string Raw => $"{IpAddress,-16} {HostName}{(string.IsNullOrWhiteSpace(Comment) ? "" : "   # " + Comment)}";
}

public sealed record HostsFileInfo(string Path, bool Exists, bool CanWrite, int EntryCount);

// ---------- 远程客户端 ----------
public sealed record RemoteClientInfo(
    string Id,
    string Title,
    string Protocol,
    bool IsAvailable,
    string? Executable,
    string? Hint);

// ---------- 防火墙 ----------
public sealed record FirewallRule(
    string Name,
    string Direction,
    string Action,
    string? Protocol,
    string? LocalPort,
    string? RemoteAddress,
    bool Enabled);

public sealed record FirewallStatus(bool Supported, string? UnsupportedReason, bool? Enabled, string? Profile, string? Engine);

// ---------- Discovery Protocol (LLDP/CDP) ----------
public sealed record DiscoveryNeighbor(
    string Interface,
    string Protocol,
    string? ChassisId,
    string? PortId,
    string? SystemName,
    string? ManagementAddress,
    string? Ttl);

// ---------- 位计算器 ----------
public sealed record BitCalculatorResult(
    bool Success,
    string? Error,
    string Binary,
    string Decimal,
    string Hexadecimal,
    string Octal,
    int BitLength);

// ---------- WiFi 详细 BSS（WLAN API + beacon/probe-response IE 解析）----------
public sealed record WifiBssDetail(
    string Ssid,
    string Bssid,
    int Channel,
    int FrequencyMhz,
    int RssiDbm,
    int SignalQuality,
    int BeaconInterval,
    int CapabilityRaw,
    string Security,
    string Cipher,
    string Akm,
    string PhyType,
    string? Vendor,
    IReadOnlyList<KeyValuePair<string, string>> IeSummary);

/// <summary>WiFi 读取权限状态：决定是否需要弹窗提权。</summary>
public sealed record WifiAccessState(
    bool CanReadBssList,
    bool RequiresElevation,
    string? BlockReason,
    string? Hint);

// ---------- 802.11 管理帧（monitor mode 原始捕获）----------
public sealed record WifiFrameInfo(
    string FrameType,
    string SubType,
    string Source,
    string Destination,
    string Bssid,
    string? Ssid,
    int? Channel,
    int? RssiDbm,
    long? Sequence,
    string? Detail);

public sealed record WifiMonitorCapability(
    bool Supported,
    string? Reason,
    string? Interface,
    string? AdapterDescription);

/// <summary>提权执行结果（提权进程无法重定向 stdout，因此结果通过文件传递）。</summary>
public sealed record ElevatedRunResult(
    bool Started,
    bool CanceledByUser,
    bool TimedOut,
    int ExitCode,
    string? Error);