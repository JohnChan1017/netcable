<# 启动应用并用内置 --screenshot 模式抓图（无窗口焦点占用，可在无人值守下运行） #>
param([string]$Out = "artifacts\ui.png", [string]$Exe = "src\NetCable.Desktop\bin\Debug\net10.0\NetCable.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $root $Exe
$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
Remove-Item $outPath -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exePath -ArgumentList "--screenshot=$outPath" -PassThru -RedirectStandardError (Join-Path $env:TEMP "nc-shot.err") -RedirectStandardOutput (Join-Path $env:TEMP "nc-shot.out")
Start-Sleep -Seconds 7
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
if (Test-Path $outPath) { Write-Host "screenshot -> $outPath ($((Get-Item $outPath).Length) bytes)" -ForegroundColor Green }
else { Get-Content (Join-Path $env:TEMP "nc-shot.err") -ErrorAction SilentlyContinue; throw "截图失败" }
