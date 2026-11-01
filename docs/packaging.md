# 打包与分发

> NetCable 提供**绿色版（免安装）**和**安装版**，三端均为**自包含发布**（自带 .NET 运行时，目标机器无需装 .NET）。

## 1. 产物一览

| 平台 | 文件 | 大小 | 用法 |
| --- | --- | --- | --- |
| Windows 绿色版 | `NetCable-1.0.0-win-x64-portable.zip` | 46.8 MB | 解压到任意目录，双击 `NetCable.exe` |
| Windows 安装版 | `NetCable-1.0.0-win-x64-setup.zip` | 46.8 MB | 解压后双击 `安装.bat`（或右键 `install.ps1` 用 PowerShell 运行） |
| Linux | `NetCable-1.0.0-linux-x64.tar.gz` | 43.3 MB | `tar -xzf` 后执行 `./netcable.sh` |
| macOS (Apple Silicon) | `NetCable-1.0.0-osx-arm64.tar.gz` | 43.5 MB | `tar -xzf` 后执行 `./netcable.sh` |

解压后约 **106–109 MB**（230 个文件），其中包含 `coreclr.dll` / `hostfxr.dll` / `System.Private.CoreLib.dll` 等运行时文件。

## 2. 安装版做了什么

`install.ps1` 是**当前用户安装**（不需要管理员）：

1. 复制文件到 `%LOCALAPPDATA%\Programs\NetCable`
2. 创建开始菜单快捷方式 + 桌面快捷方式
3. 在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetCable` 写入卸载项
   （所以能在「设置 → 应用」里看到并卸载，`EstimatedSize` 也会显示）
4. 卸载：运行安装目录下的 `uninstall.ps1`，或从「设置 → 应用」点卸载

实测结果（本机）：

```
安装到: %USERPROFILE%\AppData\Local\Programs\NetCable
完成：开始菜单/桌面已创建快捷方式，可在「设置 → 应用」中卸载。
安装目录=True 主程序=True 开始菜单=True 桌面=True 注册表=True
卸载命令: powershell -ExecutionPolicy Bypass -File "...\NetCable\uninstall.ps1"
（运行后）安装目录已移除=True 开始菜单已移除=True 桌面已移除=True 注册表已移除=True
```

## 2.1 MSI 安装包（WiX）

除了 PowerShell 安装脚本，还提供**真正的 MSI**（Windows Installer，可用组策略/SCCM 静默部署，进「应用和功能」标准卸载）。

| 项 | 值 |
| --- | --- |
| 文件 | `NetCable-1.0.0-win-x64.msi` |
| 大小 | **38.9 MB** |
| 安装范围 | **perUser**（装到 `%LOCALAPPDATA%\Programs\NetCable`，**不需要管理员**） |
| 安装界面 | 标准 WiX 向导（欢迎 → 许可 → 选择目录 → 进度 → 完成） |
| 快捷方式 | 开始菜单 `NetCable\NetCable` + 桌面 |
| 卸载 | 「设置 → 应用」→ NetCable，或 `msiexec /x <msi> /qn` |

构建：

```powershell
pwsh -File scripts/build-msi.ps1                    # 先 publish 再打 MSI
pwsh -File scripts/build-msi.ps1 -SkipPublish       # 复用 .dist/stage/win-x64
pwsh -File scripts/build-msi.ps1 -Version 1.1.0.0
```

实测（本机非管理员）：

```
msiexec /i NetCable.msi /qn   → exit=0
安装目录=True  主程序=True  文件数=230  开始菜单=True  桌面快捷方式=True
ARP: 名称=NetCable 版本=1.0.0.0 大小=111882 KB
运行截图: 167536 b
msiexec /x NetCable.msi /qn   → exit=0
安装目录已移除=True 开始菜单已移除=True 桌面已移除=True ARP 残留=无
```

### WiX 的坑（已解决）

| 坑 | 现象 | 解决 |
| --- | --- | --- |
| WiX 7 要接受 OSMF 付费协议 | `WIX7015: You must accept the Open Source Maintenance Fee (OSMF) EULA` | 改用 **WiX 5.0.2**（无此要求）；工具离线装在 `.cache/wix5` |
| 离线装扩展 | `wix extension add` 走 NuGet | 直接解压 `wixtoolset.ui.wixext` 包，用 `-ext <dll 路径>` 传 |
| 中文进不了 MSI | `WIX0311: code page 1252` | `Package/@Codepage="936"` |
| 与 WixUI 内置属性冲突 | `WIX0091: Duplicate Property WIXUI_INSTALLDIR / ARPNOMODIFY` | 删掉自己定义的这两个属性（WixUI_InstallDir 已内置） |
| `-d` 定义变量 | `WIX0118: Additional argument '-dVersion=...' unexpected` | 写成 `-d "Version=..."`（分开传） |

> 想改成 **perMachine**（装到 `Program Files`、需要 UAC）：把 `Product.wxs` 里 `Package/@Scope` 改成 `perMachine`，
> `INSTALLFOLDER` 改挂到 `ProgramFiles64Folder` 下即可。

## 3. 自己打包

```powershell
pwsh -File scripts/package.ps1                                  # 三端全部
pwsh -File scripts/package.ps1 -Runtime win-x64                 # 只打 Windows
pwsh -File scripts/package.ps1 -Version 1.1.0 -Runtime linux-x64
pwsh -File scripts/package.ps1 -SingleFile                      # 单文件 exe（体积略小，首次启动自解压）
```

产物在 `.dist/`，脚本最后会打印每个文件的大小与 SHA256。

## 4. 实现要点（踩过的坑）

| 问题 | 现象 | 解决 |
| --- | --- | --- |
| `SelfContained` 不能作为**全局属性**传 | `-p:SelfContained=true` 会让所有被引用库项目也要 `Microsoft.WindowsDesktop.App/AspNetCore.App` 运行时包，离线源里没有 → restore 失败 | 改成在 `NetCable.Desktop.csproj` 里 `<SelfContained Condition="'$(RuntimeIdentifier)' != ''">true</SelfContained>`，命令行只传 `-p:RuntimeIdentifier` |
| RID 属性级联 | 库项目也被要求 RID 专属运行时 | `ProjectReference` 上 `GlobalPropertiesToRemove=RuntimeIdentifier;RuntimeIdentifiers;SelfContained;...`（上游 NETworkManager 同款做法） |
| 跨会话 ACL | `.cache`、`%TEMP%` 下的旧目录当前会话不可写（EPERM / UnauthorizedAccess） | 构建脚本统一使用工作区内的 `.cache/{feed2,nuget2,cli-home2,tmp}` |
| 安装脚本中文乱码 | 无 BOM 的 UTF-8 被 Windows PowerShell 5.1 当 ANSI 解码，中文破坏引号解析 → `TerminatorExpectedAtEndOfString` | 生成的 `install.ps1` / `uninstall.ps1` 全部写成 **UTF-8 with BOM** |
| 打包脚本自身 | 嵌套 here-string 里的 `'@` 提前闭合 | 用文件写入而不是内联 here-string 生成脚本 |

## 5. 分发前必读

- **未做代码签名**：Windows SmartScreen 首次运行会提示"未知发布者" → 点「更多信息 → 仍要运行」；
  macOS 首次运行需 `xattr -dr com.apple.quarantine`（`netcable.sh` 已自动执行）或右键打开。
  正式分发建议购买代码签名证书（Windows EV 证书 / Apple Developer ID）。
- **不含 Npcap**（许可证限制：个人免费、再分发需 Nmap OEM 授权）。抓包相关功能见「依赖与环境」页的检测与安装引导，
  也可自行把 `npcap-*.exe` 放进程序目录 `redist\` 用一键静默安装。
- **架构限制**：当前只产出 `win-x64` / `linux-x64` / `osx-arm64`；如需 `win-arm64` / `osx-x64` 加 `-Runtime` 即可
  （需相应运行时包，见 `.cache/feed2`）。
- **未做安装包（MSI/EXE 图形安装器）**：环境中没有 Inno Setup / WiX / NSIS。
  当前方案是"ZIP + PowerShell 安装脚本"，效果等价（当前用户安装 + 快捷方式 + 卸载项）。
  若要 MSI，可后续引入 WiX 或 Inno Setup 再套一层。
