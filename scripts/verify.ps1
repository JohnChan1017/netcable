<#
  NetCable 冒烟验证：构建 + 每个工具页截图 + 可选三端交叉发布
    pwsh -File scripts/verify.ps1
    pwsh -File scripts/verify.ps1 -Publish
#>
param(
    [switch]$Publish,
    [string[]]$Tools = @()
)
$ErrorActionPreference = "Continue"
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
$dotnet = Join-Path $sdk "dotnet.exe"

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

$proj = Join-Path $root "src\NetCable.Desktop\NetCable.Desktop.csproj"
$exe = Join-Path $root "src\NetCable.Desktop\bin\Debug\net10.0\NetCable.exe"

Write-Host "== build ==" -ForegroundColor Cyan
& $dotnet msbuild $proj -t:Restore -m:1 -nodeReuse:false -v:q -nologo "-p:RestoreConfigFile=$cfg" "-p:NuGetAudit=false"
& $dotnet msbuild $proj -t:Build -m:1 -nodeReuse:false -v:q -nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

if ($Tools.Count -eq 0) {
    $Tools = @("dashboard","interfaces","wifi","ipscan","portscan","ping","traceroute","dns","rdp","powershell","ssh","vnc","webconsole","snmp","sntp","hosts","firewall","discovery","wol","rdap","whois","ipgeo","subnet","bitcalc","lookup","connections","listeners","neighbors","speedtest","httptls")
}
$smoke = Join-Path $root "artifacts\smoke"
New-Item -ItemType Directory -Force -Path $smoke | Out-Null
$results = @()
foreach ($tool in $Tools) {
    $out = Join-Path $smoke "$tool.png"
    Remove-Item $out -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $exe -ArgumentList "--screenshot=$out", "--tool=$tool", "--autorun" -PassThru `
        -RedirectStandardError (Join-Path $root "artifacts\smoke\.err") -RedirectStandardOutput (Join-Path $root "artifacts\smoke\.out")
    $wait = switch ($tool) { "dashboard" { 26 } "speedtest" { 20 } "discovery" { 20 } default { 13 } }
    Start-Sleep -Seconds $wait
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $ok = Test-Path $out
    $bytes = if ($ok) { (Get-Item $out).Length } else { 0 }
    $results += [pscustomobject]@{ Tool = $tool; Ok = $ok; Bytes = $bytes }
    Write-Host ("  {0,-14} {1}" -f $tool, $(if ($ok) { "OK ${bytes}b" } else { "FAIL" }))
}
$results | Format-Table -AutoSize | Out-String | Write-Host

if ($Publish) {
    foreach ($rid in @("win-x64", "linux-x64", "osx-arm64")) {
        $outDir = Join-Path $root "artifacts\publish\$rid"
        Write-Host "== publish $rid ==" -ForegroundColor Cyan
        & $dotnet msbuild $proj -t:Restore -m:1 -nodeReuse:false -v:q -nologo "-p:RestoreConfigFile=$cfg" "-p:NuGetAudit=false" "-p:RuntimeIdentifier=$rid"
        & $dotnet msbuild $proj -t:Publish -m:1 -nodeReuse:false -v:q -nologo `
            "-p:RuntimeIdentifier=$rid" "-p:Configuration=Release" "-p:SelfContained=false" "-p:PublishDir=$outDir\"
        if ($LASTEXITCODE -eq 0) {
            $count = (Get-ChildItem $outDir -File -ErrorAction SilentlyContinue).Count
            Write-Host "  $rid -> $outDir ($count files)" -ForegroundColor Green
        } else { Write-Host "  $rid FAILED" -ForegroundColor Red }
    }
}
