# NetCable 验收报告（M1 + M2，2026-11-01）

> 结论：**NETworkManager 的 28 个内置工具已全部还原，另加 2 个增强工具；Windows / macOS / Linux 三端可构建、可交叉发布。**

---

## 1. 规模

| 指标 | 数值 |
| --- | --- |
| 代码量（C# + Avalonia XAML） | **175 个文件 / 16,018 行** |
| 服务层 | 48 个文件 / 10,136 行（3 个项目） |
| 工具页面 | **30 个**（30 个 ViewModel + 30 个 View） |
| Core 单元测试 | 16 项，全通过 |
| 服务层自测断言 | 36(SNMP) + 16(Discovery) + 20(Hosts/Remote/Firewall) + 47(Network) + 25(Lookup) + 19(Platform) = **163 项** |

## 2. 构建验证

| 项目 | 结果 |
| --- | --- |
| `NetCable.Core` | ✅ 0 error / 0 warning |
| `NetCable.Services.Network` | ✅ |
| `NetCable.Services.Lookup` | ✅ |
| `NetCable.Services.Platform` | ✅ |
| `NetCable.Desktop`（30 个工具页） | ✅ |
| `tools/NetCable.Cli` | ✅ |
| `tests/NetCable.Tests` | ✅ 16/16 通过 |

## 3. 三端交叉发布（M2 复跑）

| RID | 结果 | 大小 | 关键原生依赖 |
| --- | --- | --- | --- |
| `win-x64` | ✅ | 32.4 MB / 43 文件 | `NetCable.exe`、`libSkiaSharp.dll`、`libHarfBuzzSharp.dll` |
| `linux-x64` | ✅ | 27.6 MB / 42 文件 | `NetCable`(ELF)、`libSkiaSharp.so`、`libHarfBuzzSharp.so` |
| `osx-arm64` | ✅ | 35.1 MB / 43 文件 | `NetCable`(Mach-O)、`libSkiaSharp.dylib`、`libHarfBuzzSharp.dylib`、`libAvaloniaNative.dylib` |

## 4. UI 验收：30/30 页面截图通过

`pwsh -File scripts/verify.ps1` → `本地冒烟截图（未入库）`（每页自动点击"开始"后渲染）

**有真实数据的页面（本机实测）**

| 页面 | 数据 |
| --- | --- |
| dashboard | 6 块网卡、网关 198.18.0.2、公网 IP 203.0.113.10 |
| interfaces | 网卡 MAC / IPv4 / IPv6 / 速率 / 状态 |
| ipgeo | US / California / AS38136 / Akari Networks |
| subnet | 192.168.1.100/24 → 254 可用主机 |
| bitcalc | 0xFF → 11111111 / 255 / FF / 377，8 bit；位运算实时计算 |
| lookup | 3C:5A:B4 → ASUSTek（库 39,691 条） |
| ipscan | 192.168.1.1 → <路由器>.local + ASUSTek MAC |
| portscan | 127.0.0.1:1-1024，135/445 开放 |
| dns | example.com A/MX 等 |
| whois | example.com 18 字段 + 原文 3,265 字符 |
| connections | 4,087 条连接 |
| listeners | 138 个监听端口 |
| neighbors | 7 条 ARP 记录 |
| hosts | 真实路径 + 只读提示（非管理员） |
| **firewall** | 状态：域/专用/公用 均启用；**705 条真实规则** |
| **rdp / ssh / powershell** | 检测到 mstsc / ssh / pwsh，命令预览正确 |
| **vnc** | 未安装 → 中文降级提示（符合预期） |
| **webconsole** | 平台分支命令预览 `start "" "url"` |
| wol | 魔术包发送成功 + OUI 厂商 |
| **discovery** | **Npcap 直连**：抓包 10s / 410 帧，LLDP 0 / CDP 0（家用路由器不发通告，属正常） |

**因本机环境受限而降级、但页面正确展示原因（非缺陷）**

| 页面 | 原因 |
| --- | --- |
| ping / traceroute | Meta Tunnel 吞 ICMP（系统 `ping` 同样失败） |
| rdap / httptls / speedtest | 本机 SChannel TLS 不可用（`SEC_E_NO_CREDENTIALS`） |
| sntp | 沙箱阻断 UDP/123 |
| wifi | Win11 24H2 非管理员无法读取 WLAN |
| snmp | 局域网无 SNMP 设备 → 超时 + 排查建议 |
| ~~discovery~~ | 已修复：Npcap 直连后端可用，实测抓包 10s / 410 帧 |

## 5. 服务层功能验证（CLI 直测）

```
> NetCable.Cli.exe subnet 192.168.1.100/24
192.168.1.0/24  mask=255.255.255.0  range=192.168.1.1-192.168.1.254  hosts=254  private=True

> NetCable.Cli.exe dns example.com A
DNS example.com A @ 192.168.1.1, 198.18.0.2  (67ms)
  A  543  172.66.147.243
  A  543  104.20.23.154

> NetCable.Cli.exe whois example.com
WHOIS example.com @ whois.verisign-grs.com
  Domain Name : EXAMPLE.COM
  Creation Date : 1995-08-14T04:00:00Z
  ...

> NetCable.Cli.exe scan 192.168.1.1-192.168.1.8
  192.168.1.1  up  8ms  <路由器>.local  3C:5A:B4:11:22:33  ASUSTek COMPUTER INC.
  192.168.1.2  down
  ...
```

## 6. M2 新增实现要点

| 工具 | 实现 |
| --- | --- |
| **SNMP** | 手写 BER 编解码（零依赖）：GET(0xA0)/GETNEXT(0xA1) WALK、v1/v2c、全类型解码（含 TimeTicks 格式化、三类异常值）、error-status 中文映射；36 项断言（含自建 UDP 161 mock agent） |
| **Discovery Protocol** | 四级后端自动选择：**Npcap 直连**（P/Invoke wpcap，无需 Wireshark）→ Wireshark dumpcap（任意磁盘）→ tcpdump → Windows 内置 pktmon；pcap 解析（大小端/纳秒 magic、VLAN、Linux SLL）+ LLDP/CDP TLV 解码；28 项断言 |
| **Hosts 编辑** | 三平台路径、解析（别名/行内注释/行号）、自动备份、临时文件原子替换、CanWrite 探测 |
| **远程客户端** | rdp/ssh/vnc/powershell 四客户端统一"探测→生成命令→启动"；目标解析支持 host / host:port / user@host / [IPv6]:port；三平台启动器（cmd start / open+osascript / x-terminal-emulator） |
| **防火墙** | 只读：netsh advfirewall → 非管理员回退注册表（本机取到 705 条真实规则）；Linux nft→iptables；macOS pfctl |
| **位计算器** | 2/8/10/16 进制互转（BigInteger 支持大数）、位长、AND/OR/XOR/位移 |

## 6.1 WiFi / 802.11 管理帧（M3）

| 能力 | 本机结果 | 前提 |
| --- | --- | --- |
| 基础 WiFi 状态（当前连接/周边 AP） | 非管理员被拒（错误 5） | 管理员权限 |
| **AP 详情 + 管理帧 IE**（SSID/BSSID/信道/RSSI/Beacon/加密套件/AKM/HT·VHT·HE/厂商 IE） | ✅ 实现完成，非管理员正确报告"需要授权（错误 5）"并给出提权按钮 | 管理员（UAC 授权） |
| **802.11 原始管理帧**（monitor mode） | ❌ 本机不支持：`pcap_can_set_rfmon` 返回 0（Intel 无线网卡 驱动） | 管理员 + 支持 monitor 的网卡 |

- 提权链路已验证：`--elevated-task wifi-bss --out x.json` 在非提权下正确返回 `Ok=false / "Windows 拒绝访问 WLAN 信息（错误 5）。需要管理员权限。"`；
  `--elevated-task wifi-monitor` 正确返回 `Ok=false / "当前网卡/驱动不支持 monitor mode（pcap_can_set_rfmon 返回 0）…适配器：Intel 无线网卡"`。
- 无法在无人值守环境验证的部分：UAC 弹窗需要用户点击，因此"提权后成功读到 AP 列表"未实机跑通（服务层已用合成 IE 做 34 项断言、x64 结构偏移全字段校验）。
- 详细技术说明见 [wifi-capture.md](wifi-capture.md)。

## 7. 仍未在真机验证的部分

- macOS / Linux 的**运行**（仅交叉编译 + 解析器级单测覆盖样例输出）
- Windows 之外的 WiFi / 邻居表 / 连接列表 / 防火墙 / 远程客户端启动
- 提权场景（管理员/root）下的 WiFi、Hosts 写入、防火墙实时规则、Discovery 抓包
- 真实 SNMP 设备；真实交换机/AP 的 LLDP/CDP 通告（家用路由器不发，本机只能验证"抓包链路正常、帧数>0"）
- UAC 提权后的 WLAN AP 详情读取（需要人工点击授权）
- 支持 monitor mode 的外置网卡上的真实 802.11 帧捕获（本机内置网卡不支持）
