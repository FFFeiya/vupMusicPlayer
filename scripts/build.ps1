# 发布到 dist\VupPlayer：主程序 + MCP 服务 + libmpv + yt-dlp
# 用法：pwsh scripts/build.ps1
# 需要本机安装 .NET 9 桌面运行时（框架依赖发布，体积小）。
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'dist\VupPlayer'

if (-not (Test-Path (Join-Path $root 'tools\libmpv-2.dll')) -or -not (Test-Path (Join-Path $root 'tools\yt-dlp.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-deps.ps1')
}

# 播放器正在从发布目录运行时不能覆盖
$running = Get-Process -Name VupPlayer, VupPlayer.Mcp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "播放器（或 MCP 服务）正在从 $out 运行，请先退出播放器（托盘右键 → 退出）再打包。"
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'src\VupPlayer.App\VupPlayer.App.csproj') -c Release -r win-x64 --self-contained false -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }
dotnet publish (Join-Path $root 'src\VupPlayer.Mcp\VupPlayer.Mcp.csproj') -c Release -r win-x64 --self-contained false -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'MCP 服务发布失败' }

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("发布完成：{0}（{1:N1} MB）" -f $out, $size)
Write-Host "注册到 Claude Code："
Write-Host ("  claude mcp add vupplayer --scope user -- `"{0}`"" -f (Join-Path $out 'VupPlayer.Mcp.exe'))
