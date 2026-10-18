# 把 dist\VupPlayer.next 换成正式版本：等播放器（和 MCP 服务）退出，覆盖 dist\VupPlayer，然后重新打开播放器。
# 用法：pwsh scripts/apply-update.ps1 [-TimeoutMinutes 120] [-NoRestart]
param([int]$TimeoutMinutes = 120, [switch]$NoRestart)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$live = Join-Path $root 'dist\VupPlayer'
$next = Join-Path $root 'dist\VupPlayer.next'
if (-not (Test-Path (Join-Path $next 'VupPlayer.exe'))) { throw "没有找到新版：$next（先运行 scripts/build.ps1）" }

function Running {
    Get-Process -Name VupPlayer, VupPlayer.Mcp -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($live, [StringComparison]::OrdinalIgnoreCase) }
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$wasRunning = [bool](Running)
if ($wasRunning) { Write-Host "等待播放器退出（托盘右键 → 退出）…" }
while (Running) {
    if ((Get-Date) -gt $deadline) { throw "等了 $TimeoutMinutes 分钟播放器还没退出，放弃。新版仍在 $next。" }
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 1   # 等文件句柄释放

# 先备份旧版，换失败时可以还原
$backup = Join-Path $root 'dist\VupPlayer.prev'
if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
if (Test-Path $live) { Rename-Item $live $backup }
try {
    Rename-Item $next $live
} catch {
    if (Test-Path $backup) { Rename-Item $backup $live }
    throw
}
Write-Host "已换上新版：$live（旧版备份在 $backup）"

if (-not $NoRestart) {
    Start-Process (Join-Path $live 'VupPlayer.exe')
    Write-Host "已重新打开播放器。"
}
