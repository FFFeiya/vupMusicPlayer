# 下载运行时依赖到 tools/：libmpv-2.dll 与 yt-dlp.exe
# 用法：pwsh scripts/fetch-deps.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null

# yt-dlp
$ytdlp = Join-Path $tools 'yt-dlp.exe'
if ($Force -or -not (Test-Path $ytdlp)) {
    Write-Host '下载 yt-dlp.exe ...'
    Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $ytdlp
}

# libmpv（shinchiro 构建）
$mpvDll = Join-Path $tools 'libmpv-2.dll'
if ($Force -or -not (Test-Path $mpvDll)) {
    $rel   = Invoke-RestMethod 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
    $asset = $rel.assets | Where-Object { $_.name -match '^mpv-dev-x86_64-\d{8}-git-.*\.7z$' } | Select-Object -First 1
    if (-not $asset) { throw '找不到 mpv-dev-x86_64 资源' }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("mpvdev_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $archive = Join-Path $tmp $asset.name
        Write-Host "下载 $($asset.name) ..."
        Invoke-WebRequest $asset.browser_download_url -OutFile $archive
        tar -xf $archive -C $tmp
        $dll = Get-ChildItem $tmp -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
        if (-not $dll) { throw '压缩包里没有 libmpv-2.dll' }
        Copy-Item $dll.FullName $mpvDll -Force
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Get-ChildItem $tools | Select-Object Name, Length
