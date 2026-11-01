<#
  NetCable 打包脚本：绿色版（免安装）+ 安装版（Windows 当前用户安装）
  用法:
    pwsh -File scripts/package.ps1                    # 三端全部打包
    pwsh -File scripts/package.ps1 -Runtime win-x64   # 只打 Windows
    pwsh -File scripts/package.ps1 -SingleFile        # 单文件 exe（体积略小，首次启动自解压）
  产物: .dist/NetCable-<ver>-<rid>-portable.zip / -setup.zip / .tar.gz
#>
param(
    [string[]]$Runtime = @("win-x64", "linux-x64", "osx-arm64"),
    [string]$Version = "1.0.0",
    [switch]$SingleFile
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root ".cache"
$sdk = Join-Path $cache "dotnet"
$feed = Join-Path $cache "feed"
$feed2 = Join-Path $cache "feed2"
$cfg = Join-Path $env:TEMP "nc-build\NuGet.Config"
$dist = Join-Path $root ".dist"
$stage = Join-Path $dist "stage"
$proj = Join-Path $root "src\NetCable.Desktop\NetCable.Desktop.csproj"

# 包缓存：优先共享目录，不可写则用会话级（跨会话 ACL 问题）
$nuget = Join-Path $cache "nuget"
try {
    New-Item -ItemType Directory -Force -Path $nuget -ErrorAction Stop | Out-Null
    $probe = Join-Path $nuget (".wprobe-" + $PID)
    New-Item -ItemType File -Path $probe -Force -ErrorAction Stop | Out-Null
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
} catch {
    $nuget = Join-Path $cache "nuget2"
    New-Item -ItemType Directory -Force -Path $nuget | Out-Null
}

New-Item -ItemType Directory -Force -Path (Split-Path $cfg) | Out-Null
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="localfeed" value="$feed" /><add key="localfeed2" value="$feed2" /></packageSources>
  <config><add key="globalPackagesFolder" value="$nuget" /></config>
</configuration>
"@
Set-Content -Path $cfg -Value $nugetConfig -Encoding UTF8

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
$dotnet = Join-Path $sdk "dotnet.exe"

New-Item -ItemType Directory -Force -Path $dist, $stage | Out-Null

function Publish-Rid([string]$rid) {
    $out = Join-Path $stage $rid
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    Write-Host "== $rid : restore ==" -ForegroundColor Cyan
    & $dotnet msbuild $proj -t:Restore -m:1 -nodeReuse:false -v:q -nologo `
        "-p:RestoreConfigFile=$cfg" "-p:NuGetAudit=false" `
        "-p:RuntimeIdentifier=$rid"
    if ($LASTEXITCODE -ne 0) { throw "restore $rid 失败" }

    Write-Host "== $rid : publish ==" -ForegroundColor Cyan
    $extra = @()
    if ($SingleFile) {
        $extra = @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true")
    }
    & $dotnet msbuild $proj -t:Publish -m:1 -nodeReuse:false -v:q -nologo `
        "-p:RuntimeIdentifier=$rid" "-p:Configuration=Release" `
        "-p:PublishDir=$out\" @extra
    if ($LASTEXITCODE -ne 0) { throw "publish $rid 失败" }

    Get-ChildItem $out -Recurse -Filter *.pdb -File | Remove-Item -Force -ErrorAction SilentlyContinue
    $redist = Join-Path $out "redist"
    New-Item -ItemType Directory -Force -Path $redist | Out-Null
    Copy-Item (Join-Path $root "redist\README.md") $redist -Force
    foreach ($doc in @("README.md", "docs\capture-backends.md", "docs\wifi-capture.md", "docs\verification.md")) {
        $src = Join-Path $root $doc
        if (Test-Path $src) { Copy-Item $src $out -Force }
    }
    return $out
}

function New-WindowsInstallerScripts([string]$dir) {
    $installLines = @(
        '# NetCable 安装脚本（当前用户安装，无需管理员）',
        '$ErrorActionPreference = ''Stop''',
        '$here = Split-Path -Parent $MyInvocation.MyCommand.Path',
        '$target = Join-Path $env:LOCALAPPDATA ''Programs\NetCable''',
        'Write-Host "安装到: $target" -ForegroundColor Cyan',
        'New-Item -ItemType Directory -Force -Path $target | Out-Null',
        'Get-ChildItem $here -File | Where-Object { $_.Name -notin @(''install.ps1'',''uninstall.ps1'',''安装.bat'') } | Copy-Item -Destination $target -Force',
        'Get-ChildItem $here -Directory | ForEach-Object { Copy-Item $_.FullName -Destination $target -Recurse -Force }',
        'Copy-Item (Join-Path $here ''uninstall.ps1'') $target -Force',
        '$exe = Join-Path $target ''NetCable.exe''',
        '$ws = New-Object -ComObject WScript.Shell',
        '$links = @((Join-Path $env:APPDATA ''Microsoft\Windows\Start Menu\Programs\NetCable.lnk''), (Join-Path ([Environment]::GetFolderPath(''Desktop'')) ''NetCable.lnk''))',
        'foreach ($lnk in $links) {',
        '    $sc = $ws.CreateShortcut($lnk)',
        '    $sc.TargetPath = $exe',
        '    $sc.WorkingDirectory = $target',
        '    $sc.IconLocation = "$exe,0"',
        '    $sc.Description = ''NetCable 跨平台网络工具箱''',
        '    $sc.Save()',
        '}',
        ('$ver = ''' + $Version + ''''),
        '$key = ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetCable''',
        'New-Item -Path $key -Force | Out-Null',
        'Set-ItemProperty $key -Name DisplayName -Value ''NetCable 网络工具箱''',
        'Set-ItemProperty $key -Name DisplayVersion -Value $ver',
        'Set-ItemProperty $key -Name Publisher -Value ''NetCable''',
        'Set-ItemProperty $key -Name InstallLocation -Value $target',
        'Set-ItemProperty $key -Name DisplayIcon -Value $exe',
        'Set-ItemProperty $key -Name NoModify -Value 1 -Type DWord',
        'Set-ItemProperty $key -Name NoRepair -Value 1 -Type DWord',
        'Set-ItemProperty $key -Name UninstallString -Value (''powershell -ExecutionPolicy Bypass -File "{0}"'' -f (Join-Path $target ''uninstall.ps1''))',
        'Set-ItemProperty $key -Name EstimatedSize -Value ([int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB))',
        'Write-Host "完成：开始菜单/桌面已创建快捷方式，可在「设置 → 应用」中卸载。" -ForegroundColor Green'
    )
    # 必须带 UTF-8 BOM：Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI 解码，中文会破坏引号解析
    [System.IO.File]::WriteAllText((Join-Path $dir "install.ps1"), ($installLines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

    $uninstallLines = @(
        '# NetCable 卸载脚本',
        '$ErrorActionPreference = ''SilentlyContinue''',
        '$target = Join-Path $env:LOCALAPPDATA ''Programs\NetCable''',
        'Get-Process -Name ''NetCable'' | Stop-Process -Force',
        'foreach ($lnk in @((Join-Path $env:APPDATA ''Microsoft\Windows\Start Menu\Programs\NetCable.lnk''), (Join-Path ([Environment]::GetFolderPath(''Desktop'')) ''NetCable.lnk''))) { Remove-Item $lnk -Force }',
        'Remove-Item ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetCable'' -Recurse -Force',
        'Remove-Item $target -Recurse -Force',
        'Write-Host "NetCable 已卸载。" -ForegroundColor Green'
    )
    [System.IO.File]::WriteAllText((Join-Path $dir "uninstall.ps1"), ($uninstallLines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

    $batLines = @(
        '@echo off',
        'chcp 65001 >nul',
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"',
        'pause'
    )
    Set-Content -Path (Join-Path $dir "安装.bat") -Value ($batLines -join "`r`n") -Encoding UTF8
}

function New-UnixLauncher([string]$dir) {
    $shLines = @(
        '#!/usr/bin/env sh',
        '# NetCable 启动脚本（Linux/macOS）',
        'cd "$(dirname "$0")"',
        'if [ "$(uname)" = "Darwin" ]; then',
        '  xattr -dr com.apple.quarantine . 2>/dev/null || true',
        'fi',
        'chmod +x ./NetCable 2>/dev/null || true',
        'exec ./NetCable "$@"'
    )
    Set-Content -Path (Join-Path $dir "netcable.sh") -Value ($shLines -join "`n") -Encoding UTF8 -NoNewline
}

$results = @()
foreach ($rid in $Runtime) {
    $out = Publish-Rid $rid
    $sizeMb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

    if ($rid -eq "win-x64") {
        New-WindowsInstallerScripts $out
        $setupZip = Join-Path $dist "NetCable-$Version-win-x64-setup.zip"
        Remove-Item $setupZip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $setupZip -CompressionLevel Optimal
        foreach ($n in @("install.ps1", "uninstall.ps1", "安装.bat")) { Remove-Item (Join-Path $out $n) -Force -ErrorAction SilentlyContinue }
        $portableZip = Join-Path $dist "NetCable-$Version-win-x64-portable.zip"
        Remove-Item $portableZip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $portableZip -CompressionLevel Optimal
        $results += [pscustomobject]@{
            Rid = $rid; UnpackedMB = $sizeMb
            ArtifactMB = [math]::Round((Get-Item $portableZip).Length / 1MB, 1)
            Files = "portable.zip / setup.zip"
        }
    }
    else {
        New-UnixLauncher $out
        $tar = Join-Path $dist "NetCable-$Version-$rid.tar.gz"
        Remove-Item $tar -Force -ErrorAction SilentlyContinue
        $payload = Join-Path $stage "$rid-root"
        $target = Join-Path $payload "NetCable-$Version-$rid"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item (Join-Path $out "*") $target -Recurse -Force
        tar -czf $tar -C $payload "NetCable-$Version-$rid"
        Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue
        $results += [pscustomobject]@{
            Rid = $rid; UnpackedMB = $sizeMb
            ArtifactMB = [math]::Round((Get-Item $tar).Length / 1MB, 1)
            Files = "tar.gz"
        }
    }
}

Write-Host ""
Write-Host "== 打包结果 ==" -ForegroundColor Green
$results | Format-Table -AutoSize | Out-String | Write-Host
Get-ChildItem $dist -File -Filter "NetCable-*" | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    Write-Host ("  {0,-44} {1,7:N1} MB  SHA256 {2}" -f $_.Name, ($_.Length / 1MB), $hash)
}
