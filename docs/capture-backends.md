# Discovery Protocol 的抓包后端说明

> 结论先放这里：**用 LLDP/CDP 抓邻居，需要的是"二层抓包能力"，不是 Wireshark。**
> Windows 上有四条路，NetCable 全部支持并自动选择。

---

## 1. 为什么一定要抓包？

LLDP（EtherType `0x88CC`）和 CDP（Cisco SNAP `0x2000`，目的 MAC `01:00:0c:cc:cc:cc`）
都是**以太网二层帧**，不经过 IP 层。

所以这些手段统统拿不到：

| 手段 | 为什么不行 |
| --- | --- |
| `Socket` / `TcpListener` | 只能看到 IP 层及以上 |
| `WMI` / `Get-NetAdapter` / `netsh` | 只能拿到网卡配置，拿不到帧 |
| `ping` / `arp` / DHCP | 拿不到交换机/AP 的通告帧 |

必须走**网卡驱动层的包捕获**。Windows 上有 4 种实现方式，对应 NetCable 的 4 个后端。

---

## 2. NetCable 的四个后端（按优先级自动选择）

| 优先级 | 后端 | 依赖 | 需要管理员 | 说明 |
| --- | --- | --- | --- | --- |
| 1 | **Npcap 直连** | Npcap 驱动（免费） | 通常否* | 直接 P/Invoke `wpcap.dll`（`pcap_open_live` / `pcap_next_ex`），最轻量，只需装驱动 |
| 2 | **Wireshark dumpcap** | Wireshark（自带 dumpcap） | 否 | 调用 `dumpcap` 抓包到临时 pcap，再本地解析 |
| 3 | **tcpdump** | macOS 自带 / Linux 安装 | 通常是 | `tcpdump -w` 到临时 pcap |
| 4 | **Windows 内置 pktmon** | Windows 10 1809+ 自带 | **是** | `pktmon start --capture` → `etl2pcap`，**零安装**的兜底 |

\* Npcap 安装时若勾选仅管理员可访问，则需要提权；本机实测（Npcap 1.88）**非管理员可正常抓包**。

界面会显示当前实际使用的后端，例如：

```
抓包能力可用，后端：Npcap 直连
抓包 10s，共 410 帧，其中 LLDP 0 / CDP 0
```

---

## 3. 上游 NETworkManager 是怎么做的？

上游的 Discovery Protocol 调用 `PSDiscoveryProtocol` 模块。看过它的源码后可以确认：

```powershell
# PSDiscoveryProtocol 的抓包机制（节选）
New-NetEventSession -Name $SessionName -LocalFilePath $ETLFilePath
Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceType -eq 6 }
Add-NetEventPacketCaptureProvider ...
Start-NetEventSession ...
```

它用的是 **Windows 内置的 NDIS 事件捕获（`NetEventSession`）**，抓包落到 `.etl` 文件再解析。

| | 上游 PSDiscoveryProtocol | NetCable |
| --- | --- | --- |
| 抓包机制 | Windows NDIS NetEventSession（ETL） | Npcap / dumpcap / tcpdump / pktmon（pcap） |
| 需要 Wireshark | ❌ 不需要 | ❌ 不需要（Npcap 即可；pktmon 甚至零安装） |
| 需要 Npcap | ❌ 不需要 | ✅ 推荐（也可用 pktmon 走零安装路线） |
| 权限 | **必须管理员** | Npcap 通常不需要；pktmon 需要 |

所以"NETworkManager 不依赖 Wireshark"这个说法是对的，但原因不是"不需要抓包"，
而是**它用了 Windows 自带的抓包通道**。两层事实要分开看。

---

## 4. 抓不到邻居 ≠ 工具坏了

LLDP/CDP 是**网管协议**，由交换机、企业级 AP、路由器主动发出：

- **家用路由器（如 ASUS 家用路由器）通常完全不发** → 抓到 0 条属正常
- 交换机每 30s（LLDP）或 60s（CDP）发一次 → 至少监听 30~60 秒才有机会抓到
- 抓不到时页面会明确提示"抓包链路正常，但网段内没有发送通告的设备"

判断"抓包链路是否正常"看两处：**后端名称** + **总帧数**（>0 说明确实在读网卡）。

---

## 5. 常见问题

**Q：我必须装 Wireshark 吗？**
不用。只装 [Npcap](https://npcap.com/) 驱动即可（安装时勾选 *WinPcap API-compatible*）。
如果连 Npcap 都不想装，以管理员身份运行 NetCable，会自动走 Windows 内置 `pktmon`。

**Q：我已经装了 Wireshark，还要装 Npcap 吗？**
Wireshark 安装过程本来就会装 Npcap（默认勾选）。装了 Wireshark 的话，NetCable 会自动找到它自带的 `dumpcap`，无需额外操作。

**Q：Wireshark 安装时的 TShark / extcap 组件要勾吗？**
对 NetCable **都不需要**——我们只调用 `dumpcap`（抓包）或直接调 `wpcap.dll`，不会调用 `tshark` 做协议解析（LLDP/CDP 解析是 NetCable 自己实现的）。
TShark/extcap 只有在你想手工用 Wireshark 调试时才有意义。

**Q：需要勾 "Support raw 802.11 traffic" 吗？**
只有当你想抓 802.11 管理帧（WiFi 信道/客户端关联分析）时才需要。NetCable 目前不需要。

**Q：为什么非管理员也能抓？**
Npcap 1.88 默认安装 **"Restrict Npcap driver's access to Administrators only" 未勾选**时，普通用户可抓包；
本项目实测就是这种情况。如果勾了，则需要以管理员运行 NetCable（页面会提示）。
