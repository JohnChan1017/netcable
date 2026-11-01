# WiFi / 802.11 管理与帧捕获说明

> 一句话：**读 AP 详情和 802.11 管理帧信息，在 Windows 上必须提权；原始帧捕获还需要网卡支持 monitor mode。**

---

## 1. 三条能力，三套前提

| 能力 | 数据来源 | 前提 | 本机（Windows 11 24H2 + Intel 无线网卡） |
| --- | --- | --- | --- |
| ① 基础 WiFi 状态（当前 SSID / 信号） | `netsh wlan` | 系统 WLAN 服务 | 需提权（本机非管理员返回错误 5） |
| ② AP 详情 + **管理帧 IE**（SSID/BSSID/信道/RSSI/beacon 间隔/加密套件/AKM/HT·VHT·HE/厂商 IE） | WLAN API `WlanGetNetworkBssList` | **管理员权限** | ✅ 提权后可读 |
| ③ **802.11 原始管理帧**（probe request / auth / assoc / deauth…） | Npcap monitor mode | 管理员 + **网卡驱动支持 monitor mode** + Npcap 勾选 raw 802.11 | ❌ Intel 无线网卡 驱动不支持 |

关键区别：② 和 ③ 都能拿到"管理帧信息"，但 **② 是驱动从 beacon/probe-response 里上报的 IE**，
**③ 是网卡进入监听模式后的原始帧**。② 不需要特殊网卡，③ 需要。

---

## 2. 本机实测证据

```
# WLAN API（非管理员）
WlanOpenHandle        = 0   (成功)
WlanEnumInterfaces    = 0   (成功，1 个接口 "WLAN")
WlanGetNetworkBssList = 5   (ERROR_ACCESS_DENIED)   ← 卡在这里

# netsh wlan show networks（非管理员）
Network shell commands need location permission to access WLAN information.
（但 ConsentStore\location = Allow，HKCU NonPackaged = Allow —— 说明不是定位权限，是权限令牌）

# monitor mode（Npcap 1.88 已装、服务 Running）
tshark -I -i 5 -a duration:3
→ The capture session could not be initiated (Generic error)   ← 网卡/驱动不支持
```

---

## 3. 提权流程（用户点两次即可）

```
用户点「以管理员权限读取」
   ↓
应用内确认对话框（说明为什么需要、会触发系统授权）
   ↓
Windows UAC 对话框（系统级）
   ↓
应用以管理员身份重启自身（同 exe，headless 模式：--elevated-task）
   ↓
读取 WLAN/抓包 → 结果写 JSON 临时文件 → 退出
   ↓
GUI 读取 JSON → 表格展示 → 删除临时文件
```

设计要点：

- **只把需要权限的那一步提权**，主界面仍以普通用户运行（最小权限原则）。
- 提权进程无法重定向 stdout，所以结果通过临时 JSON 文件回传（用后即删）。
- 用户在 UAC 点"否"时不会崩溃，界面显示"已取消，未获得管理员权限"。
- 跨平台：
  | 系统 | 授权方式 |
  | --- | --- |
  | Windows | `ProcessStartInfo.Verb = "runas"` → UAC |
  | macOS | `osascript ... with administrator privileges` → 系统授权框 |
  | Linux | `pkexec`（polkit）→ 桌面授权框 |

---

## 4. 如果想在 Windows 上真的抓原始 802.11 帧

需要同时满足：

1. **网卡/驱动支持 monitor mode**
   - ❌ 绝大多数笔记本内置网卡（Intel / Realtek / Broadcom 的 Windows 驱动）**不支持**
   - ✅ 常见可行方案：外置 USB 网卡（Atheros AR9271、Ralink RT3070/5572、MediaTek MT7612U 等）+ 对应驱动
2. **Npcap 安装时勾选** *Support raw 802.11 traffic (and monitor mode) for wireless adapters*
3. **以管理员身份运行**

NetCable 会先做能力探测（`pcap_can_set_rfmon`），不满足时直接给原因，不会让用户白点。

---

## 5. 常见问题

**Q：为什么开了定位服务还是不行？**
Windows 11 24H2 对 WLAN 查询加了额外校验，本机 ConsentStore 已经是 Allow 仍返回错误 5，
说明实际拦截点是权限令牌（非管理员）。所以走提权，而不是改定位设置。

**Q：提权后安全吗？**
应用只在提权实例里做"读 WLAN 信息 / 抓包 / 写结果文件"这一件事，不加载 UI、不执行用户输入的命令。
主进程仍是普通权限。代码位置：`Desktop/Services/ElevatedBridge.cs`。

**Q：macOS / Linux 也要提权吗？**
- macOS：CoreWLAN 读 SSID 需要定位权限；monitor mode 抓包需要 root。
- Linux：`iw scan` / monitor mode 需要 root（或 `CAP_NET_ADMIN` / `CAP_NET_RAW`）。
所以同一套提权流程在三个平台都适用。

---

## 6. Npcap 的 dot11 支持与本机实测（2026-11）

Npcap 官方文档对 `/dot11_support` 的说明（原文要点）：

> installs a second Lightweight Filter Driver that uses the **Native WiFi API** to capture raw 802.11 frames
> on devices that are put into network monitor mode. Captured frames are given a Radiotap header.
> **Not all hardware or network drivers support the Native WiFi API.**

本机核查结果：

| 检查项 | 结果 | 说明 |
| --- | --- | --- |
| `%WINDIR%\System32\Npcap\WlanHelper.exe` | ✅ 存在 | **说明 dot11 支持已经装上了**（Wireshark 安装时勾选了 raw 802.11） |
| `wpcap.dll` FileVersion | 1.10.6 | 这是 WinPcap **API 版本**，不是 Npcap 产品版本（应用已改为读注册表显示 1.88） |
| `pcap_can_set_rfmon` | 0 | 适配器驱动不支持 monitor mode |
| `tshark -I`（monitor 模式） | 失败（Generic error） | 同上 |
| `WlanHelper "WLAN" modes` | 报 adapter name 错误 | 需要管理员令牌才能发 OID 请求，未在无提权下验证 |

**结论**：Npcap 侧组件齐全，卡在**网卡驱动**——Intel 无线网卡 的 Windows 驱动不提供 Native WiFi monitor mode。

## 7. 要不要买网卡？

| 你的目标 | 需要买网卡吗 | 说明 |
| --- | --- | --- |
| **AP 列表 + 管理帧 IE**（SSID/BSSID/信道/RSSI/Beacon/CCMP/AKM/HT·VHT·HE/厂商 IE） | ❌ **不需要** | 走 Windows WLAN API，内置网卡就够；只差**管理员授权**（UAC） |
| **802.11 原始管理帧**（probe request / auth / deauth / assoc…） | ✅ 需要 | 必须是"Windows 驱动支持 Native WiFi monitor mode"的网卡；内置 无线网卡 不行 |

### 选购建议（按可靠性排序）

1. **最稳：Linux 路线（不换电脑不换系统，U 盘启动 Kali）**
   需要网卡在 Linux 下有 monitor-mode 驱动，常见成熟型号：
   - Atheros **AR9271**（如 TP-Link TL-WN722N **仅 v1**）
   - MediaTek **MT7612U**（如 ALFA AWUS036ACM）
   - Realtek **RTL8812AU**（如 ALFA AWUS036ACH，需 aircrack-ng 驱动）
   Linux 下可完整做 monitor + 注入（deauth 等），生态最成熟。

2. **Windows 路线（不推荐但可行）**
   - 前提：网卡**厂商驱动**支持 Native WiFi monitor mode，且 Npcap 勾选 `/dot11_support`
   - 验证方法：装好后以管理员运行 `WlanHelper <接口名> modes`，输出里含 `monitor` 才算可用
   - 社区反馈里 Netgear / ALFA 部分 USB 型号可用，但**没有官方支持清单**，建议先买可退换的
   - 限制：Windows 上**没有数据包注入**，做不了 deauth 攻击/握手包抓取（只能被动看）

3. **只为看 AP 列表/加密信息** → 什么都不用买，点 UAC 就行。