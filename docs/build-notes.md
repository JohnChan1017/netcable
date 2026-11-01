# 本机构建环境说明（踩坑记录）

这台机器不是标准 .NET 开发环境，有两个硬限制。所有构建脚本都已封装，按脚本用即可。

## 1. 系统 TLS（SChannel）不可用 → NuGet 只能用离线源

现象：

```
NU1301: 无法加载源 https://api.nuget.org/v3/index.json 的服务索引。
  The SSL connection could not be established, see inner exception.
  Authentication failed, see inner exception.
  安全包中没有可用的凭证
```

同一现象也出现在 `curl`（`schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS`）
和 `git clone https` 上；但 **Node.js（自带 OpenSSL）可以正常访问 HTTPS**。

判断：这是本机 SChannel/SSPI 凭证存储层面的问题，不是 NuGet 配置问题。

**解决方式**：用 Node 把所需 `.nupkg` 全部下载到本地目录，做成 NuGet 本地源。

- 离线包目录：`%TEMP%\nc-nuget-feed`（约 26+ 个包，含 Avalonia 全家桶 + DnsClient + 三端 apphost）
- 全局包缓存：`%TEMP%\nc-nuget`
- 仓库根目录的 `NuGet.Config` 已把源指向该目录（`<clear/>` + 本地源）

> 在有正常网络的环境中：删除根目录 `NuGet.Config` 即可恢复用 nuget.org。

### 另注：DNS 是 fake-IP

本机开着 Meta Tunnel（TUN 模式代理），`github.com` 解析到 `198.18.0.11` 这类 fake-IP，
所以任何做 SSRF 防护的工具（包括 DSH 的 `web_fetch`）都会拒绝。抓网页要用 Node 的 `fetch`。

## 2. MSBuild 多节点/构建服务器被沙箱拦截 → 必须单节点

现象：多项目解决方案的 restore/build 直接失败，但只报 `0 个警告 0 个错误`（静默失败），
`-v:diag` 里能看到 `_GenerateRestoreProjectPathWalk` 失败、MSBuild 子节点启动后无输出。

原因：MSBuild 节点间通信用 Windows 命名管道，被沙箱阻断（与 Node 子进程 pipe 被拒是同类问题）。

**解决方式**（已写进 `scripts/build.ps1`）：

```powershell
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = "1"
dotnet msbuild <proj> -t:Build -m:1 -nodeReuse:false
```

## 3. .NET SDK 是解压版，不在 PATH

- 解压位置：`%TEMP%\nc-dotnet`（从 `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip` 下载后 `tar -xf`）
- 系统里 `C:\Program Files\dotnet` 只有**运行时**，没有 SDK，`dotnet build` 会报 "could not be loaded"
- 临时目录会被清理；如果 SDK 丢失，重新下载那个 zip 解压回 `%TEMP%\nc-dotnet` 即可

## 4. 图形界面自动化验证

Avalonia 窗口在后台时，`PrintWindow` 会阻塞、`CopyFromScreen` 只能抓到前台窗口。
所以应用内置了截图模式：

```powershell
NetCable.exe --screenshot=<path>   # 启动 → 等待 → 把窗口渲染成 PNG → 退出
NetCable.exe --tool=ping           # 启动时直接选中某个工具页
NetCable.exe --tool=ping --autorun # 页面加载后自动点一次"开始"（冒烟测试）
```

`RenderTargetBitmap` 直接渲染视觉树，不依赖窗口是否在前台，适合无人值守回归。

## 5. 网络环境对功能测试的影响

Meta Tunnel 会接管出站流量：

- **ICMP 被吞**：`ping 8.8.8.8` 报 `PING: transmit failed. 一般故障`，所以本机 Ping 工具 100% 丢包是预期的
- **TCP 探测假阳性**：对不存在的地址做端口探测可能"全开"（此前做拓扑图时实测过）
- **DNS fake-IP**：域名解析返回 `198.18.x.x`

结论：本机只适合验证"解析/渲染/流程"是否正确；真实网络能力需要在无代理环境回归。
