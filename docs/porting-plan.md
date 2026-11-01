# NETworkManager → 跨平台移植方案（NetCable）

> 目标：把 [BornToBeRoot/NETworkManager](https://github.com/BornToBeRoot/NETworkManager)（WPF / .NET 10 / Windows-only）
> 变成 **Windows + macOS + Linux** 三端可用的桌面应用。
>
> 上游许可证：**GPL-3.0**。本移植同样以 GPL-3.0 发布，保留原作者署名。

---

## 一、结论：能做，但这是"重写 UI 层"，不是"改配置"

原项目规模（`main` 分支实测）：

| 指标 | 数值 |
| --- | --- |
| C# 文件 / 代码行 | 814 / **99,141** |
| XAML 文件 / 代码行 | 165 / **41,622** |
| 项目数 | 13 |
| 目标框架 | **全部 `net10.0-windows`**（含 Models / Utilities） |
| UI 技术栈 | WPF + MahApps.Metro + Dragablz + WebView2 + LiveCharts.WPF |
| Windows 专属依赖 | RDP ActiveX（MSTSCLib）、PowerShell SDK、CsWinRT、WinForms |

关键结论：**连"领域模型层"都引用了 WPF**，所以不存在"只换 UI 框架"的做法。但好消息是**逻辑层的 WPF 耦合很浅**：

| 层 | WPF 耦合 | 处理方式 |
| --- | --- | --- |
| `Models/Network`（111 文件：Ping/PingMonitor/PortScanner/IPScanner/DNSLookup/Traceroute/BitCalculator…） | **仅 1 个文件** | 逻辑可复用，命令构造按 OS 分支 |
| `Models/RDAP`、`Lookup`、`IPApi`、`Export`、`Firewall`、`Profiles`、`Settings` | **0** | 直接可复用 |
| `Views`（114 个 XAML）+ `ViewModels`（118 个） | 重度（MahApps 控件、Dispatcher、MessageBox） | **必须重写** |
| `Controls`（自绘控件、多选 DataGrid） | 重度 | 重写 |
| RemoteDesktop / PuTTY / PowerShell / WiFi / Firewall | Windows API（MSTSCLib、WLAN API、WMI） | 改为调用各平台原生客户端/命令 |

因此我们采用的路线是 **Avalonia + 重新分层的核心服务**：
上层 UI 全部重写，业务能力按"跨平台服务接口"重新实现，Windows 专属能力做成平台分支或"调用系统客户端"。

---

## 二、目标架构

```
NetCable.slnx
├─ src/NetCable.Core            # 跨平台基础层（net10.0，零 UI 依赖）
│   ├─ Models/                  # DTO：PingRequest/PingReplyInfo/TracerouteHop/PortScanResult/DnsRecordInfo…
│   ├─ Contracts/               # 服务接口：IPingService / ITracerouteService / IDnsLookupService / IWifiService…
│   ├─ Platform/PlatformInfo    # OS 判定
│   ├─ Platform/ProcessRunner   # 外部命令执行（超时/取消/流式）
│   ├─ Utils/                   # SubnetCalculator / OuiLookup / PortServiceLookup
│   └─ Resources/               # 复用上游数据：OUI.xml(3.8MB) / Ports.xml(1.9MB) / WhoisServers.xml
├─ src/NetCable.Services.Network   # Ping / Traceroute / PortScanner / HostScanner / WakeOnLan
├─ src/NetCable.Services.Lookup    # DNS / Whois / RDAP / IP 归属 / SNTP / HTTP-TLS / 测速
├─ src/NetCable.Services.Platform  # 网卡 / 网关 / DNS / WiFi / 连接 / 监听 / 邻居表
├─ src/NetCable.Desktop         # Avalonia 11 应用（三端单可执行）
│   ├─ Views/MainWindow.axaml   # 侧边栏 + 内容区外壳
│   ├─ ViewModels/Tools/*.cs    # 每个工具一个 VM（[ToolPage("id")] 约定式注册）
│   └─ Views/Tools/*.axaml      # 每个工具一个视图
└─ tests/                       # 冒烟与单元验证
```

**关键设计（为了并行开发与后续维护）**

1. **工具页约定式发现**：`MainWindowViewModel` 扫描程序集中带 `[ToolPage("ping")]` 的 ViewModel，
   用 `ViewLocator`（`XxxViewModel` → `XxxView`）自动挂载。新增一个工具 = 新增两个文件，**不修改任何共享文件**。
2. **统一的状态机**：所有工具继承 `ToolViewModelBase`，自带 `RunCommand / CancelCommand / IsRunning / Log / Rows`，
   UI 统一用 `Controls/LogView`、`Controls/ResultTable` 渲染。
3. **平台适配收敛在服务层**：UI 完全不知道当前是 Windows 还是 Linux。
4. **零 UI 依赖的 Core**：Core 可在 Linux/macOS 上单独构建与测试（CI 友好）。

---

## 三、工具移植矩阵（30 个：上游 28 个全部还原 + 2 个增强）

| # | 工具 | 状态 | 三端策略 |
| --- | --- | --- | --- |
| 1 | Dashboard 仪表盘 | ✅ | 网卡 + 网关 + DNS + 公网 IP 汇总 |
| 2 | Network Interface 网络接口 | ✅ | `ipconfig` / `ip addr` / `ifconfig` |
| 3 | WiFi | ✅ | `netsh wlan` / `system_profiler` / `nmcli`+`iw` |
| 4 | IP Scanner 局域网扫描 | ✅ | ICMP + TCP + ARP 三证据判活 |
| 5 | Port Scanner 端口扫描 | ✅ | TCP connect（三端一致，无需特权） |
| 6 | Ping Monitor | ✅ | Windows 托管 ICMP；unix 系统 ping |
| 7 | Traceroute 路由追踪 | ✅ | `tracert` / `traceroute` / `tracepath` |
| 8 | DNS Lookup | ✅ | DnsClient（跨平台库） |
| 9 | Remote Desktop (RDP) | ✅ | 调系统客户端：`mstsc` / `xfreerdp` / `open` |
| 10 | PowerShell | ✅ | `powershell` / `pwsh` |
| 11 | PuTTY → SSH | ✅ | 三端统一系统 `ssh` |
| 12 | TigerVNC | ✅ | `vncviewer` / macOS Screen Sharing |
| 13 | Web Console | ✅ | 调默认浏览器（Windows `start` / macOS `open` / Linux `xdg-open`） |
| 14 | SNMP | ✅ | **手写 BER**（GET/GETNEXT WALK，v1/v2c），零依赖 |
| 15 | SNTP Lookup | ✅ | UDP 123 手写 SNTP |
| 16 | Hosts File Editor | ✅ | 读写 + 自动备份；Windows/Unix 路径分支 |
| 17 | Firewall 防火墙 | ✅ | 只读：`netsh advfirewall` / `nft` / `pfctl` |
| 18 | Discovery Protocol (LLDP/CDP) | ✅ | 解析 pcap（dumpcap/tcpdump），自写 LLDP/CDP TLV 解码 |
| 19 | Wake on LAN | ✅ | UDP 魔术包（三端一致） |
| 20 | RDAP | ✅ | HTTP 查询 rdap.org |
| 21 | Whois | ✅ | TCP 43 + IANA referral |
| 22 | IP Geolocation | ✅ | ip-api.com（失败回退 ipify/ipapi.co） |
| 23 | Subnet Calculator | ✅ | 纯计算 |
| 24 | Bit Calculator | ✅ | 纯计算（2/8/10/16 进制互转 + 位运算） |
| 25 | Lookup (OUI) | ✅ | 内置 OUI 库 39,691 条 |
| 26 | Connections | ✅ | `netstat -ano` / `ss -tunap` / `netstat -anv`+`lsof` |
| 27 | Listeners | ✅ | 同上（LISTEN 过滤） |
| 28 | Neighbor Table | ✅ | `arp -a` / `ip neigh` / `arp -an` |
| 29 | **测速（增强）** | ✅ | Cloudflare 端点，三端一致 |
| 30 | **HTTP/TLS 检查（增强）** | ✅ | 状态码 / 重定向链 / 证书链 |

图例：✅ 已实现。远程客户端类工具统一采用"**检测本机客户端 → 生成命令 → 一键启动**"模式，不内置协议实现，
因此三端都不会"瘸"：Windows 用 mstsc/powershell/OpenSSH，macOS 用 open/osascript，Linux 用 xfreerdp/x-terminal-emulator。

> 说明：M2 已完成全部 28 个上游工具 + 2 个增强工具的 UI 与服务层实现。
> 其中 Discovery Protocol 需要 libpcap（Windows 装 Npcap/Wireshark），Hosts 写入/防火墙读取需要管理员权限——
> 权限不足时页面会显示明确原因，不会崩溃。
## 四、里程碑

- **M1（已完成 ✅）**：Avalonia 外壳 + 核心服务层 + **18 个工具页面**；三端构建 + 交叉发布验证（win-x64 / linux-x64 / osx-arm64）；自动截图验收链路；CLI 版；Core 单元测试。验收证据见 [verification.md](verification.md)。
- **M2（已完成 ✅）**：补齐 Bit Calculator / 测速 / HTTP-TLS / SNMP / Hosts 编辑 / Firewall / Discovery Protocol；远程客户端类工具统一为"调用系统原生客户端"；工具总数达到 30（上游 28 + 2 增强）。
- **M3**：打包发布（Windows MSI / macOS .app+dmg / Linux AppImage+deb+rpm）、自动更新、CI 三平台矩阵构建。

---

## 五、构建与运行

本机环境有两个特殊限制（详见 `docs/build-notes.md`）：

1. 系统 TLS（SChannel）不可用 → NuGet 只能用本地离线源；
2. MSBuild 多节点/常驻构建服务器依赖命名管道，被沙箱拦截 → 必须 `-m:1`。

因此统一用脚本（脚本里已经封装好）：

```powershell
# 构建
pwsh -File scripts/build.ps1                                        # 默认构建 Desktop 入口
pwsh -File scripts/build.ps1 -Project src/NetCable.Core             # 只构建某个项目

# 运行
src\NetCable.Desktop\bin\Debug\net10.0\NetCable.exe

# 无头截图（UI 回归验收）
pwsh -File scripts/screenshot.ps1 -Out artifacts/ui.png
```

**正常联网的机器上**（macOS/Linux/Windows 均可）：

```bash
dotnet build src/NetCable.Desktop/NetCable.Desktop.csproj
dotnet run  --project src/NetCable.Desktop
```

> 若在正常机器上构建，删除根目录 `NuGet.Config`（它把源指向了本机临时离线缓存）。

---

## 六、已知限制

- M1 的三端验证方式是**交叉编译 + RID 发布**，未在 macOS/Linux 真机运行过；平台分支按各系统命令手册实现，需真机回归。
- 本机 Meta Tunnel VPN 会吞掉 ICMP、劫持 DNS fake-IP，导致 Ping/端口扫描在本机出现"全超时/全开放"的假象——这是环境问题，不是实现问题（系统自带 `ping` 同样失败）。
- 上游的 `Discovery Protocol`（LLDP/CDP）与 `SNMP` 需要额外原生依赖，M1 未纳入。
