param([string]$ProjectRoot = "D:\heiweilu\DSH-NEW\common\jxr-hdr-converter")
$ErrorActionPreference = 'Stop'
$repo = 'https://repo.msys2.org/mingw/mingw64/'
$dir = Join-Path $ProjectRoot '.scratch\msys2'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$packages = @(
    'libavif-', 'aom-', 'dav1d-', 'zlib-', 'libpng-', 'libjpeg-turbo-',
    'libxml2-', 'rav1e-', 'libwebp-', 'svt-av1-', 'libyuv-', 'libiconv-',
    'libgcc-', 'libstdc%2B%2B-', 'winpthreads-git-'
)

$html = (Invoke-WebRequest -Uri $repo -UseBasicParsing -TimeoutSec 120).Content
foreach ($p in $packages) {
    $latest = [regex]::Matches($html, "mingw-w64-x86_64-$p`w*[0-9a-z\.\-]*any\.pkg\.tar\.zst") |
        ForEach-Object { $_.Value } | Select-Object -Unique | Select-Object -Last 1
    if (-not $latest) { Write-Output "MISSING $p"; continue }
    $out = Join-Path $dir ([uri]::UnescapeDataString($latest))
    if (Test-Path $out) { Write-Output "cached $($latest)"; continue }
    Write-Output "fetch $latest"
    Invoke-WebRequest -Uri ($repo + $latest) -OutFile $out -UseBasicParsing -TimeoutSec 600
    tar -xf $out -C $dir
}

$msysBin = Join-Path $dir 'mingw64\bin'
$tools = Join-Path $ProjectRoot 'src\hdrshot\bin\Release\net8.0-windows\win-x64\tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Copy-Item (Join-Path $msysBin '*.dll') $tools -Force
foreach ($exe in 'avifenc.exe', 'avifdec.exe') {
    Copy-Item (Join-Path $msysBin $exe) $tools -Force
}
Write-Output "=== tools dir ==="
(Get-ChildItem $tools -Name) -join ', '
