<#
  构建 NetCable 的 Windows MSI（WiX 5）
  用法:
    pwsh -File scripts/build-msi.ps1                          # perUser（默认，不需要管理员）
    pwsh -File scripts/build-msi.ps1 -Scope perMachine        # 装到 Program Files，需要管理员
    pwsh -File scripts/build-msi.ps1 -SkipPublish             # 复用 .dist/stage/win-x64
    pwsh -File scripts/build-msi.ps1 -Version 1.1.0
  产物: .dist/NetCable-<ver>-win-x64[-perMachine].msi
#>
param(
    [string]$Version = "1.0.0",
    [ValidateSet("perUser", "perMachine")]
    [string]$Scope = "perUser",
    [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root ".cache"
$sdk = Join-Path $cache "dotnet"
$wix = Join-Path $cache "wix5\wix.exe"
$uiExt = Join-Path $cache "wix5-ext\wixext5\WixToolset.UI.wixext.dll"
$utilExt = Join-Path $cache "wix5-ext\wixext5\WixToolset.Util.wixext.dll"
$dist = Join-Path $root ".dist"
$stage = Join-Path $dist "stage\win-x64"
$wxs = Join-Path $root "installer\Product.wxs"
$icon = Join-Path $root "src\NetCable.Desktop\Assets\netcable.ico"
$license = Join-Path $root "installer\License.rtf"
$suffix = if ($Scope -eq "perMachine") { "-perMachine" } else { "" }
$outMsi = Join-Path $dist "NetCable-$Version-win-x64$suffix.msi"

if (-not (Test-Path $wix)) { throw "未找到 WiX 工具: $wix（见 docs/packaging.md 的离线安装步骤）" }
if (-not (Test-Path $uiExt)) { throw "未找到 WiX UI 扩展: $uiExt" }

if (-not $SkipPublish) {
    Write-Host "== 重新发布 win-x64（自包含）==" -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $root "scripts\package.ps1") -Version $Version -Runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw "package.ps1 失败" }
}

if (-not (Test-Path $stage)) { throw "publish 目录不存在: $stage" }

# MSI 不需要 portable 安装脚本和调试符号
foreach ($n in @("install.ps1", "uninstall.ps1", "安装.bat")) {
    Remove-Item (Join-Path $stage $n) -Force -ErrorAction SilentlyContinue
}
Get-ChildItem $stage -Recurse -Filter *.pdb -File | Remove-Item -Force -ErrorAction SilentlyContinue

$env:DOTNET_ROOT = $sdk
$env:PATH = "$sdk;$env:PATH"
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
$env:TEMP = Join-Path $cache "tmp"
$env:TMP = $env:TEMP

Write-Host "== wix build（Scope=$Scope）==" -ForegroundColor Cyan
& $wix build $wxs -o $outMsi `
    -arch x64 `
    -d "Version=$Version" `
    -d "SourceDir=$stage" `
    -d "IconPath=$icon" `
    -d "LicenseRtf=$license" `
    -d "InstallScope=$Scope" `
    -ext $uiExt `
    -ext $utilExt
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$size = [math]::Round((Get-Item $outMsi).Length / 1MB, 1)
$hash = (Get-FileHash $outMsi -Algorithm SHA256).Hash
Write-Host ""
Write-Host "== MSI 产物 ==" -ForegroundColor Green
Write-Host ("  {0}  {1} MB  SHA256 {2}" -f (Split-Path $outMsi -Leaf), $size, $hash)
if ($Scope -eq "perMachine") {
    Write-Host "  安装: msiexec /i `"$outMsi`"        (perMachine，会弹 UAC；装到 Program Files)"
    Write-Host "  静默: msiexec /i `"$outMsi`" /qn    (需管理员)"
    Write-Host "  校验包: msiexec /a `"$outMsi`" TARGETDIR=<dir> /qn   (行政安装，仅解包，不需要管理员)"
} else {
    Write-Host "  安装: msiexec /i `"$outMsi`"        (perUser，不需要管理员)"
    Write-Host "  静默: msiexec /i `"$outMsi`" /qn"
}
Write-Host "  卸载: 设置 → 应用 → NetCable"
