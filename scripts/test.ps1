<#
  运行 NetCable 单元测试（Core 层，零外部依赖）
    pwsh -File scripts/test.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root ".cache"
$sdk = Join-Path $cache "dotnet"
$feed = Join-Path $cache "feed"
$feed2 = Join-Path $cache "feed2"
$nuget = Join-Path $cache "nuget"
$cfg = Join-Path $env:TEMP "nc-build\NuGet.Config"
$proj = Join-Path $root "tests\NetCable.Tests\NetCable.Tests.csproj"
$exe = Join-Path $root "tests\NetCable.Tests\bin\Debug\net10.0\NetCable.Tests.exe"

try {
    New-Item -ItemType Directory -Force -Path $nuget -ErrorAction Stop | Out-Null
    $probe = Join-Path $nuget (".wprobe-" + $PID)
    New-Item -ItemType File -Path $probe -Force -ErrorAction Stop | Out-Null
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
} catch {
    $nuget = Join-Path $env:TEMP "nc-nuget"
}

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
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = "1"
$env:DOTNET_NOLOGO = "1"
$dotnet = Join-Path $sdk "dotnet.exe"

& $dotnet msbuild $proj -t:Restore -m:1 -nodeReuse:false -v:q -nologo "-p:RestoreConfigFile=$cfg" "-p:NuGetAudit=false"
if ($LASTEXITCODE -ne 0) { Write-Host "RESTORE FAILED" -ForegroundColor Red; exit 1 }
& $dotnet msbuild $proj -t:Build -m:1 -nodeReuse:false -v:q -nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

& $exe
exit $LASTEXITCODE
