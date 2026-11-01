<#
  NetCable 离线构建脚本（本机两个硬限制都在这里处理）
    1. 系统 TLS(SChannel) 不可用 → NuGet 用工作区内的离线源 .cache/feed
    2. MSBuild 多节点依赖命名管道被沙箱拦截 → 强制单节点

  用法（任意工作目录均可）:
    pwsh -File scripts/build.ps1                          # 构建 Desktop 入口（含全部依赖）
    pwsh -File scripts/build.ps1 -Project src/NetCable.Core
    pwsh -File scripts/build.ps1 -Project tools/NetCable.Cli -Configuration Release
#>
param(
    [string]$Project = "",
    [string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root ".cache"
$sdk = Join-Path $cache "dotnet"
$feed = Join-Path $cache "feed"
$feed2 = Join-Path $cache "feed2"
$nuget = Join-Path $cache "nuget"
# 共享缓存目录里只有 SDK/feed/nuget；NuGet.Config 放本会话 TEMP（跨会话 ACL 只允许创建者写）
$cfg = Join-Path $env:TEMP "nc-build\NuGet.Config"
# 共享包缓存可能不可写（跨会话 ACL），不可写时自动降级到会话级缓存
$nuget = Join-Path $cache "nuget"
try {
    New-Item -ItemType Directory -Force -Path $nuget -ErrorAction Stop | Out-Null
    $probe = Join-Path $nuget (".wprobe-" + $PID)
    New-Item -ItemType File -Path $probe -Force -ErrorAction Stop | Out-Null
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
} catch {
    $nuget = Join-Path $env:TEMP "nc-nuget"
    Write-Host "[build] 共享包缓存不可写，改用会话级缓存: $nuget" -ForegroundColor Yellow
}

$dotnetExe = Join-Path $sdk "dotnet.exe"
if (-not (Test-Path $dotnetExe)) { throw "离线 .NET SDK 缺失：$dotnetExe" }
if (-not (Test-Path $feed)) { throw "离线 NuGet 源缺失：$feed" }

New-Item -ItemType Directory -Force -Path (Split-Path $cfg) | Out-Null
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="localfeed" value="$feed" /><add key="localfeed2" value="$feed2" /></packageSources>
  <config><add key="globalPackagesFolder" value="$nuget" /></config>
</configuration>
"@ | Set-Content -Path $cfg -Encoding UTF8

$env:DOTNET_ROOT = $sdk
$env:PATH = "$sdk;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $cache "cli-home2"
# 本机 %TEMP% 根目录带旧会话 ACL，MSBuild/NuGet 在其中建临时目录会被拒 → 固定到工作区
$ncTmp = Join-Path $cache "tmp"
New-Item -ItemType Directory -Force -Path $ncTmp | Out-Null
$env:TEMP = $ncTmp
$env:TMP = $ncTmp
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = $nuget
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = "1"

$target = if ($Project) {
    if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
} else {
    Join-Path $root "src\NetCable.Desktop\NetCable.Desktop.csproj"
}

Write-Host "restore $target ..." -ForegroundColor Cyan
& $dotnetExe msbuild $target -t:Restore -m:1 -nodeReuse:false -v:m -nologo "-p:RestoreConfigFile=$cfg" "-p:NuGetAudit=false"
if ($LASTEXITCODE -ne 0) { Write-Host "RESTORE FAILED" -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host "build $target ($Configuration) ..." -ForegroundColor Cyan
& $dotnetExe msbuild $target -t:Build -m:1 -nodeReuse:false -v:m -nologo "-p:Configuration=$Configuration"
exit $LASTEXITCODE
