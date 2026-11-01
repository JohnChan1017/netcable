# redist/ —— 可选的本机安装包目录

NetCable **不随应用分发** Npcap（许可证限制），但支持你在自己的机器上放置安装包，让应用一键静默安装。

## 使用方法

1. 从 <https://npcap.com/#download> 下载 `npcap-x.y.z.exe`
2. 放到本目录（文件名为 `npcap*.exe` 即可被自动识别）
3. 打开 NetCable → 「依赖与环境」→ 点击「静默安装 Npcap」
4. 安装参数：`/S /dot11_support=yes /admin_only=no`（静默 + 启用 802.11 支持 + 普通用户可抓包）
5. 安装完成后点「重新检测」

## 许可证提醒

- Npcap 对**个人/教育用途**免费
- **再分发**（把自己的软件连同 Npcap 安装包一起发给别人）需要 Nmap 的 **Npcap OEM 授权**
- 因此本目录默认不包含安装包，`.gitignore` 已忽略 `redist/*.exe`

## 什么时候需要 Npcap？

| 场景 | 需要 Npcap？ |
| --- | --- |
| 读取 AP 列表 + 管理帧 IE | ❌ 不需要（Windows WLAN API，但需要管理员权限） |
| LLDP/CDP 邻居发现 | ⚠️ 可选（可用 Windows 内置 pktmon 替代） |
| 802.11 原始管理帧（monitor mode） | ✅ 需要（且网卡驱动必须支持 monitor mode） |
