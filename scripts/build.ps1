# 发布到 dist\VupPlayer：主程序 + MCP 服务 + libmpv + yt-dlp
# 用法：pwsh scripts/build.ps1
# 需要本机安装 .NET 9 桌面运行时（框架依赖发布，体积小）。
#
# 播放器正在从 dist\VupPlayer 运行时，不会覆盖它，而是发布到 dist\VupPlayer.next，
# 然后运行 scripts/apply-update.ps1：等你退出播放器后自动换上新版并重新打开。
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$live = Join-Path $root 'dist\VupPlayer'

if (-not (Test-Path (Join-Path $root 'tools\libmpv-2.dll')) -or -not (Test-Path (Join-Path $root 'tools\yt-dlp.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-deps.ps1')
}

$running = Get-Process -Name VupPlayer, VupPlayer.Mcp -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($live, [StringComparison]::OrdinalIgnoreCase) }
$out = if ($running) { Join-Path $root 'dist\VupPlayer.next' } else { $live }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'src\VupPlayer.App\VupPlayer.App.csproj') -c Release -r win-x64 --self-contained false -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }
dotnet publish (Join-Path $root 'src\VupPlayer.Mcp\VupPlayer.Mcp.csproj') -c Release -r win-x64 --self-contained false -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'MCP 服务发布失败' }

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("发布完成：{0}（{1:N1} MB）" -f $out, $size)
if ($running) {
    Write-Host "播放器正在运行，新版先放在 $out。"
    Write-Host "运行 pwsh scripts/apply-update.ps1，退出播放器后会自动换上新版并重新打开。"
} else {
    Write-Host "注册到 Claude Code："
    Write-Host ("  claude mcp add vupplayer --scope user -- `"{0}`"" -f (Join-Path $out 'VupPlayer.Mcp.exe'))
}
