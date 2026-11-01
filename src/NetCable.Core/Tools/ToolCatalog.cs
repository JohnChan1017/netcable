namespace NetCable.Core.Tools;

public enum ToolGroup
{
    Overview,
    Network,
    Diagnostics,
    Lookup,
    System,
    Remote
}

public enum ToolStatus
{
    Implemented,
    Planned,
    PlatformLimited
}

public sealed record ToolDefinition(
    string Id,
    string Title,
    ToolGroup Group,
    string Description,
    ToolStatus Status = ToolStatus.Implemented,
    string? PlatformNote = null);

/// <summary>
/// 工具清单：完整还原上游 NETworkManager 的 28 个内置工具，另加 2 个增强工具（测速、HTTP/TLS）。
/// 上游顺序：Dashboard, Network Interface, WiFi, IP Scanner, Port Scanner, Ping Monitor, Traceroute,
/// DNS Lookup, Remote Desktop, PowerShell, PuTTY, TigerVNC, Web Console, SNMP, SNTP Lookup,
/// Hosts File Editor, Firewall, Discovery Protocol, Wake on LAN, RDAP, Whois, IP Geolocation,
/// Subnet Calculator, Bit Calculator, Lookup, Connections, Listeners, Neighbor Table.
/// </summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } = new List<ToolDefinition>
    {
        // 1
        new("dashboard", "仪表盘", ToolGroup.Overview, "本机网络总览：网卡、网关、DNS、公网出口与常用入口"),
        // 2
        new("about", "关于与更新", ToolGroup.Overview, "版本信息、检查更新与一键下载安装（GitHub Releases）"),
        new("interfaces", "网络接口", ToolGroup.Network, "列出全部网卡、IP 地址、掩码、速率与状态"),
        // 3
        new("wifi", "WiFi", ToolGroup.System, "当前连接与周边 AP 扫描（信号、信道、加密）"),
        // 4
        new("ipscan", "局域网扫描", ToolGroup.Diagnostics, "发现网段内存活主机、MAC、厂商与主机名"),
        // 5
        new("portscan", "端口扫描", ToolGroup.Diagnostics, "TCP 端口探测与服务识别，支持并发与自定义端口段"),
        // 6
        new("ping", "Ping", ToolGroup.Diagnostics, "ICMP 连通性、延迟、丢包与抖动，支持连续探测"),
        // 7
        new("traceroute", "路由追踪", ToolGroup.Diagnostics, "逐跳 RTT 与路径，自动识别到达目标"),
        // 8
        new("dns", "DNS 查询", ToolGroup.Lookup, "A/AAAA/MX/TXT/NS/CNAME/SOA/PTR/SRV/CAA，可指定服务器"),
        // 9
        new("rdp", "远程桌面 (RDP)", ToolGroup.Remote, "调用系统 RDP 客户端连接目标", PlatformNote: "Windows: mstsc；Linux: xfreerdp；macOS: Windows App/Microsoft Remote Desktop"),
        // 10
        new("powershell", "PowerShell / pwsh", ToolGroup.Remote, "调用系统 PowerShell / pwsh 控制台", PlatformNote: "Windows 用 powershell，其余用 pwsh"),
        // 11
        new("ssh", "SSH / PuTTY", ToolGroup.Remote, "调用系统 SSH 客户端连接目标", PlatformNote: "三平台统一使用系统 ssh"),
        // 12
        new("vnc", "VNC", ToolGroup.Remote, "调用 VNC 客户端连接目标", PlatformNote: "需安装 vncviewer / TigerVNC"),
        // 13
        new("webconsole", "Web 控制台", ToolGroup.Remote, "在内置面板中打开设备管理页（调用系统浏览器）", PlatformNote: "三平台统一调用默认浏览器"),
        // 14
        new("snmp", "SNMP", ToolGroup.Diagnostics, "SNMP v1/v2c GET 与 WALK，查询设备信息与 OID 树"),
        // 15
        new("sntp", "NTP 时间", ToolGroup.Diagnostics, "SNTP 时间同步与往返延迟"),
        // 16
        new("hosts", "Hosts 编辑", ToolGroup.System, "查看/编辑系统 hosts 文件（写入前自动备份，需管理员权限）"),
        // 17
        new("firewall", "防火墙", ToolGroup.Remote, "查看防火墙状态与规则", PlatformNote: "Windows: netsh advfirewall；Linux: nftables；macOS: pfctl"),
        // 18
        new("discovery", "Discovery Protocol", ToolGroup.Diagnostics, "被动监听 LLDP/CDP 邻居通告（自动选择抓包后端）", PlatformNote: "后端优先级：Npcap 直连（Windows 推荐，仅需驱动）→ Wireshark dumpcap → tcpdump → Windows 内置 pktmon（需管理员）"),
        // 19
        new("deps", "依赖与环境", ToolGroup.System, "检测 Npcap / Wireshark / dumpcap / tcpdump / pktmon 等抓包依赖（不随应用分发 Npcap，可引导安装）"),
        new("wol", "Wake on LAN", ToolGroup.System, "发送魔术包唤醒局域网设备"),
        // 20
        new("rdap", "RDAP", ToolGroup.Lookup, "结构化注册数据（Whois 的 JSON 后继协议）"),
        // 21
        new("whois", "Whois", ToolGroup.Lookup, "域名/IP 注册信息，自动跟随 IANA referral"),
        // 22
        new("ipgeo", "IP 归属", ToolGroup.Network, "查询公网/指定 IP 的国家、城市、ASN 与运营商"),
        // 23
        new("subnet", "子网计算器", ToolGroup.Network, "CIDR 与掩码换算、网段范围、可用主机数"),
        // 24
        new("bitcalc", "位计算器", ToolGroup.Network, "二进制/十进制/十六进制/八进制互转与位运算"),
        // 25
        new("lookup", "MAC 厂商", ToolGroup.System, "按 MAC/OUI 前缀查厂商，内置 OUI 库（39,691 条）"),
        // 26
        new("connections", "连接列表", ToolGroup.System, "当前 TCP/UDP 连接与对应进程"),
        // 27
        new("listeners", "监听端口", ToolGroup.System, "本机监听中的端口与进程"),
        // 28
        new("neighbors", "邻居表", ToolGroup.System, "ARP/邻居缓存：同网段 IP ↔ MAC 映射"),

        // ---- 增强工具（上游没有，按需求追加）----
        new("speedtest", "测速", ToolGroup.Diagnostics, "下行/上行带宽与延迟测试（Cloudflare 端点）"),
        new("httptls", "HTTP / TLS", ToolGroup.Diagnostics, "响应状态、重定向链、证书链与到期时间"),
    };

    public static IEnumerable<IGrouping<ToolGroup, ToolDefinition>> ByGroup => All.GroupBy(t => t.Group);
}
