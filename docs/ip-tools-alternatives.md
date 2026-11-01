# 跨平台「IP Tools 类」网络工具箱 · 选型报告

> 目标：找一个和安卓 **IP Tools: Network Utilities** 功能类似、能在 **Windows / macOS / Linux** 上用的桌面网络工具。
> 核实时间：2026-11-01。价格与平台支持以官方页面为准，可能变动。

---

## 0. 结论（TL;DR）

**没有一款「成熟 + 三平台通吃 + 功能全」的 IP Tools 平替。** 原因是这类软件的长相分三派：

| 派别 | 代表 | 问题 |
| --- | --- | --- |
| 功能全的一站式 GUI | NETworkManager、IP-Tools(KS-Soft)、NetScanTools | **只支持 Windows** |
| 三平台都有的 GUI | NetDia、Fing Desktop、Angry IP Scanner、Nmap/Zenmap | 要么很新、要么只覆盖单一能力（局域网或端口） |
| 三平台 CLI | nmap、trippy、dig、whois、mtr | 强，但不像 IP Tools 那样有个界面 |

**按你的情况给三条路：**

1. **主力是 Windows** → 装 **NETworkManager**。28 个内置工具，覆盖度比 IP Tools 还高，开源免费、中文界面、8.8k★。这是"最省事且最强"的一步到位。
2. **必须三平台都有 GUI** → **NetDia**（Rust/Tauri，Win/Mac/Linux，MIT）。功能骨架完全对味，但项目很新（GitHub 10★），缺 DNS/WHOIS/WiFi，适合当主力 + 别的小工具补齐。
3. **要最稳的三平台组合** → **NetDia + Angry IP Scanner + Nmap/Zenmap + Trippy + Sniffnet + 系统自带命令**，各自负责一段，全免费。

---

## 1. IP Tools 到底提供什么

[IP Tools: Network Utilities](https://play.google.com/store/apps/details?id=com.ddm.iptools)（开发者 Dmitrii Dudarenko / IP Tools Network Utilities，1000 万+ 下载，4.6★，含广告与内购）。官方功能表：

- **Ping**
- **Wi-Fi & LAN Scanner**（局域网设备发现 + Wi-Fi 分析）
- **Port Scanner**
- **DNS Lookup**
- **WHOIS**
- **Traceroute**（可选跳数）
- **WiFi Analyzer**（信道 / 信号强度 / 干扰）
- **My IP**（本机 / 内网 / 公网 IP、ISP、地理位置）
- **Router Setup**（路由器管理页、WOL 唤醒设备）
- **Connection Log / Netstat statistics**
- **IP Calculator**（子网计算）
- **IP & Host Converter**
- **Vendors Database**（MAC 厂商库）
- 导出：txt / csv / json / xls / pdf / html / xml / md

一句话：**它是一把"网络瑞士军刀"**，单个功能都不深，赢在"一个 App 全都有"。

---

## 2. 候选逐个点评

### 2.1 NetDia —— 最像 IP Tools 的三平台 GUI ⭐ 首推自研替代

- 仓库：[shellrow/netdia](https://github.com/shellrow/netdia)（Rust + Tauri，MIT，GitHub 10★，2026-10 仍在提交）
- 平台：**macOS（签名 DMG，可自动更新）/ Windows（Microsoft Store）/ Linux（AppImage、deb、rpm）**
- 功能：
  - 网卡总览（IP、网关、实时流量）、实时流量曲线（RX/TX、AVG/MAX）
  - 测速（下载 / 上传，带实时进度）
  - **邻居扫描**（局域网设备发现）、主机扫描
  - 路由表（网关、metric）
  - 套接字连接（TCP/UDP + 进程信息）
  - 公网 IP 信息（IPv4/IPv6、**ASN**、国家）
  - Ping：**ICMP / TCP / UDP / QUIC 四种协议**
  - Traceroute：ICMP / UDP，逐跳 RTT 可视化
  - 端口扫描：常用端口 / Top1000 / 自定义
- 缺：DNS 查询、WHOIS、WiFi 分析、子网计算器、WoL、导出
- 评价：**方向完全正确，就是太年轻**。适合先装上试试手感；出问题当"半成品"看。

### 2.2 NETworkManager —— 功能最全，但只支持 Windows ⭐ Windows 首选

- 仓库：[BornToBeRoot/NETworkManager](https://github.com/BornToBeRoot/NETworkManager)（.NET，开源，8,859★）
- 平台：**仅 Windows**（MSI / 便携版 / zip，有签名、支持 Winget / Chocolatey / Evergreen）
- **28 个内置工具**（官方文档列出）：
  Dashboard、Network Interface、WiFi、IP Scanner、Port Scanner、Ping Monitor、Traceroute、DNS Lookup、Remote Desktop、PowerShell、PuTTY、TigerVNC、Web Console、SNMP、SNTP Lookup、Hosts File Editor、Firewall、Discovery Protocol(LLDP/CDP)、**Wake on LAN**、**RDAP**、**Whois**、IP Geolocation、**Subnet Calculator**、Bit Calculator、Lookup、Connections、Listeners、Neighbor Table
- 还有：分组/配置档案（可加密）、16+ 语言、深浅色主题、企业级策略下发
- 评价：**这是桌面上"最像 IP Tools 甚至更强"的东西**——IP Tools 的每一项它基本都有，还多出 SNMP、LLDP/CDP、RDP/SSH/VNC。唯一缺点：Windows only。

### 2.3 Fing Desktop —— 设备发现 + 监控最强，Win/Mac

- 官网：[fing.com/products/fing-desktop](https://www.fing.com/products/fing-desktop/)
- 平台：**Windows、macOS**；**Linux 明确不支持**（Linux 上只有 Fing Agent，是 24/7 监控代理，不是桌面 GUI）
- 功能：设备发现与识别（厂商/型号/操作系统）、一键隔离陌生设备、定时安全扫描、自动测速并记录、在线时间线、实时告警、ping/traceroute/DNS check
- 价格：个人免费；24/7 监控与深度安全检查需订阅
- 评价：**"谁在我网里 + 家宽健康度"做得最好**，但需要注册账号、数据上云；无端口扫描、无 WHOIS、无 WiFi 分析。

### 2.4 Angry IP Scanner —— 三平台局域网/端口扫描老将

- 官网：[angryip.org](https://angryip.org/)（版本 3.12.0，GPLv2，5,182★）
- 平台：**Windows / macOS（ARM+Intel，macOS 11+）/ Linux（deb/rpm/jar）**；需 Java 21+（Win/Mac 安装包自带运行时，Linux 需自装 OpenJDK 21+）
- 功能：任意 IP 段快速扫描、Ping 存活、主机名反查、**MAC + 厂商**、端口扫描、NetBIOS 信息（计算机名/工作组/登录用户）、Web 服务识别、插件扩展、收藏网段；导出 CSV / TXT / XML / IP-Port 列表
- 评价：**局域网发现这一档的性价比之王**，跨平台、轻、免安装可用。但它只做"扫描"，没有 traceroute/DNS/WHOIS/WiFi。

### 2.5 Nmap + Zenmap —— 端口/服务/系统的天花板

- 官网：[nmap.org/download](https://nmap.org/download.html)（7.993；Windows / macOS / Linux；NPSL 许可，类 GPL）
- 功能：主机发现、**端口扫描**、服务与版本识别、**操作系统识别**、NSE 脚本引擎（漏洞/枚举/审计）、输出 XML/JSON
- GUI：Zenmap（官方提供 Python wheel，三平台可跑），但界面老、更新慢
- 注意：Windows 版需装 **Npcap**；部分扫描要管理员/root；部分功能在受限账号下不可用
- 评价：**不是 IP Tools 的替代品，而是"IP Tools 里端口扫描那个格子的专业版"**。配合 Angry IP Scanner 足够。

### 2.6 Trippy —— traceroute 的终端天花板

- 官网：[trippy.rs](https://trippy.rs/)（8,125★）
- 平台：**Linux / macOS / Windows / \*BSD**（x86_64、aarch64、arm7）
- 功能：ICMP / UDP / TCP over IPv4/IPv6 追踪、逐跳统计（丢包、抖动、RTT 分布）、GeoIP 世界地图、可配置参数、TUI 界面
- 评价：**看路由质量最好的工具**，纯键盘操作，没有 GUI。

### 2.7 Sniffnet —— 三平台流量监控

- 官网：[sniffnet.app](https://sniffnet.app/)（41,800★，免费开源）
- 平台：**Windows / macOS / Linux**
- 功能：实时带宽、按应用/主机/协议查看连接、过滤器、通知、导入导出 pcap
- 评价：**补"网络到底在跑什么"这一块**，和 IP Tools 的 Connection Log 定位接近但强得多。

### 2.8 只支持 Windows 的老牌共享套件（备选）

- **IP-Tools（KS-Soft）**：Windows 上 20 合 1 的老牌网络工具集，和 IP Tools 定位几乎一致。
- **NetScanTools Pro**：[netscantools.com](https://www.netscantools.com/)，商业套件，诊断类工具很全。
- 这两个都只在 Windows，且收费/共享，**不如直接上 NETworkManager**。

---

## 3. 能力覆盖矩阵

图例：✅ 原生支持　⚠️ 支持但不完整 / 要命令或脚本 / 需额外组件　— 不支持

| 能力 | NetDia | NETworkManager | Fing Desktop | Angry IP Scanner | Nmap+Zenmap | Trippy | Sniffnet | 系统命令 |
| --- | :--: | :--: | :--: | :--: | :--: | :--: | :--: | :--: |
| 三平台 | ✅ | ❌ 仅 Win | ❌ Win/Mac | ✅ | ✅ | ✅ | ✅ | ✅ |
| 图形界面 | ✅ | ✅ | ✅ | ✅ | ⚠️ Zenmap 老旧 | ❌ TUI | ✅ | ❌ |
| 本机/公网 IP、ASN | ✅ | ✅ | ✅ | — | — | — | — | ⚠️ |
| Ping | ✅ 4 协议 | ✅ 持续监控 | ✅ | ✅ 扫描 | ⚠️ | — | — | ✅ |
| Traceroute | ✅ | ✅ | ✅ | — | ⚠️ | ✅✅ | — | ✅ |
| 端口扫描 | ✅ Top1000 | ✅ | — | ✅ | ✅✅ | — | — | ⚠️ |
| 局域网设备发现 | ✅ | ✅ | ✅✅ | ✅✅ | ✅ | — | — | ⚠️ |
| DNS 查询 | — | ✅ | ⚠️ 仅检查 | — | ⚠️ 脚本 | — | — | ✅ dig |
| WHOIS / RDAP | — | ✅ | — | — | ⚠️ | — | — | ✅ whois |
| WiFi 分析 | — | ✅ | ⚠️ | — | — | — | — | ⚠️ 命令 |
| 流量/带宽监控 | ✅ | ⚠️ | ⚠️ 测速 | — | — | — | ✅✅ | ✅ |
| 抓包 | — | — | — | — | — | — | ⚠️ 导出 | ✅ tcpdump/Wireshark |
| 子网计算器 | — | ✅ | — | — | — | — | — | — |
| Wake on LAN | — | ✅ | — | — | — | — | — | — |
| SNMP / LLDP/CDP | — | ✅ | — | — | ⚠️ 脚本 | — | — | ⚠️ |
| 开源免费 | ✅ MIT | ✅ | ⚠️ 免费+订阅 | ✅ GPL | ✅ | ✅ | ✅ | ✅ |
| 成熟度 | ⚠️ 很新 | ✅ 8.8k★ | ✅ 商业产品 | ✅ 5.2k★ | ✅✅ | ✅ 8.1k★ | ✅ 41.8k★ | ✅ |

---

## 4. 三平台落地组合

### 方案 A：你在 Windows 上（最省事）

```
NETworkManager  ← 一站式，覆盖 90% 需求（含 WiFi 分析、WoL、子网计算、WHOIS）
Wireshark       ← 需要抓包时
```
一次性装完，几乎不用组合其他工具。

### 方案 B：Windows + macOS + Linux 都要用

```
统一底座：NetDia（三平台 GUI：网卡、流量、测速、邻居扫描、Ping、Traceroute、端口扫描）
局域网发现：Angry IP Scanner（三平台）
端口/服务：Nmap + Zenmap（三平台，Win 记得装 Npcap）
路由质量：Trippy（三平台 TUI）
流量监控：Sniffnet（三平台）
DNS/WHOIS：系统自带 dig / nslookup / whois，或网页版（见第 5 节）
WiFi 分析：Windows/macOS 用 NetSpot（无 Linux）；Linux 用 nmcli / iw / wavemon
抓包：Wireshark（三平台）
测速：Speedtest CLI 或 speed.cloudflare.com
```

### 方案 C：CLI 原教旨（免安装、脚本友好）

```
nmap -sn 192.168.1.0/24          # 局域网存活
nmap -sV -p- <host>              # 端口 + 服务指纹
mtr / trippy <host>              # 逐跳质量
dig +trace example.com           # DNS 链路
whois example.com ; curl -I ...  # WHOIS / HTTP 头
iperf3 -c <server>               # 带宽
```

---

## 5. 零安装网页版（跨系统、跨设备，但探测从"服务器"发起）

| 站点 | 用途 |
| --- | --- |
| [hackertarget.com](https://hackertarget.com/) | 在线 ping / traceroute / 端口扫描 / DNS（**从它的服务器发起，测不到你的内网**） |
| [mxtoolbox.com](https://mxtoolbox.com/) | DNS、SPF/DKIM、黑名单、HTTP 检查 |
| [bgp.he.net](https://bgp.he.net/) | ASN / 路由 / WHOIS / IP 归属，看"这条 IP 属于谁"最直观 |
| [ipinfo.io](https://ipinfo.io/) · [ip-api.com](https://ip-api.com/) | 公网 IP 归属、ASN、经纬度 |
| [speed.cloudflare.com](https://speed.cloudflare.com/) | 浏览器内测速，延迟/抖动/丢包/上下行 |
| [ping.pe](https://ping.pe/) | 从全球多点同时 ping/traceroute 一个目标 |

⚠️ 网页版受浏览器沙箱限制：**不能做 ICMP、不能扫本机局域网、不能扫本机端口**。这也是为什么"本地跑一个后端"的自研方案有存在价值。

---

## 6. 实战注意事项（踩过的坑）

1. **VPN / 代理 TUN 会污染扫描结果**
   本机开着 Meta Tunnel 之类的 TUN 网卡时，出站连接会被接管。本工作区早前的实测记录：对一个**并不存在**的 `192.168.1.250` 做 TCP 探测，结果"所有端口都开放"——完全是假阳性。
   → 做端口扫描 / 局域网扫描前，先确认默认路由走的是物理网卡（Windows `route print`，Linux `ip route`，macOS `netstat -rn`）；nmap 可用 `-e <网卡>` 或 `--iflist` 指定出口。

2. **权限**
   - 原始套接字（真 ICMP、SYN 扫描）在 Linux/macOS 需要 root/sudo，Windows 需要管理员 + Npcap。
   - Linux 下 `nmcli dev wifi list` / `iw dev ... scan` 扫 WiFi 通常需要 root 或 polkit 授权。
   - macOS 新版对 Wi-Fi 信息读取有限制，`airport` 工具在较新系统已移除，改用 `system_profiler SPAirPortDataType`。

3. **Nmap 在 Windows** 必须先装 Npcap；否则只能做无特权的 connect 扫描。

4. **Fing / NetSpot 这类商用工具**，免费版功能受限、需要账号、部分数据上云；敏感环境要留意。

---

## 7. 附：如果自研一个「本地 Web 版 IP Tools」

已确认的形态：**Python 标准库后端（零第三方依赖）+ 浏览器界面**，一个命令启动，Windows / macOS / Linux 通用。可行性高，功能清单如下：

| 模块 | 说明 |
| --- | --- |
| 我的网络 | 网卡、IP/掩码/网关/DNS、公网 IP + ASN + 归属、Wi-Fi 连接详情 |
| Ping | 连续探测、丢包/延迟/抖动统计、逐包时间线 |
| Traceroute | 逐跳 RTT 表，可选跳数与协议 |
| 端口扫描 | 常用 / Top1000 / 自定义，并发、服务识别、Banner |
| 局域网发现 | ARP + ICMP + PTR 反查 + MAC 厂商库，**可复用 NetCable 风格拓扑图** |
| DNS 查询 | A/AAAA/CNAME/MX/TXT/NS/SOA/PTR/SRV/CAA，可指定 DNS 服务器 |
| WHOIS / RDAP | 域名与 IP 归属，自动跟随 referral |
| HTTP / TLS | 状态码、重定向链、证书链与到期、安全响应头 |
| WiFi 分析 | 周边 AP、信号、信道、加密方式（按平台调 netsh / system_profiler / nmcli） |
| 连接列表 | TCP/UDP 连接、监听端口、对应进程 |
| 测速 | 延迟 / 下行 / 上行 |
| 子网计算器 | CIDR ↔ 掩码、主机范围、可用数量 |
| Wake on LAN | 魔术包唤醒 |
| 导出 | CSV / JSON |

技术要点：HTTP 长连接 + NDJSON 流式输出（长任务实时刷）、工具模块自注册（加功能 = 加一个文件）、只绑 `127.0.0.1`、启动自动开浏览器。

---

## 8. 参考链接

- IP Tools（安卓原文）· <https://play.google.com/store/apps/details?id=com.ddm.iptools>
- NetDia · <https://github.com/shellrow/netdia> · <https://apps.microsoft.com/detail/9NLQ03PT1DXQ>
- NETworkManager · <https://github.com/BornToBeRoot/NETworkManager> · <https://borntoberoot.net/NETworkManager/docs/introduction>
- Fing Desktop · <https://www.fing.com/products/fing-desktop/>
- Angry IP Scanner · <https://angryip.org/> · <https://github.com/angryip/ipscan>
- Nmap · <https://nmap.org/download.html>
- Trippy · <https://trippy.rs/> · <https://github.com/fujiapple852/trippy>
- Sniffnet · <https://sniffnet.app/> · <https://github.com/GyulyVGC/sniffnet>
- NetSpot（WiFi 分析，Win/Mac/Android/iOS）· <https://www.netspotapp.com/>
- NetScanTools Pro（Windows 商业套件）· <https://www.netscantools.com/>
