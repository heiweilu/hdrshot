param([string]$ProjectRoot = "")
$ErrorActionPreference = 'Stop'
# default: repo root = parent of this script's directory (works on any machine / path with spaces)
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
Write-Output "project root: $ProjectRoot"

$repo = 'https://repo.msys2.org/mingw/mingw64/'
$dir = Join-Path $ProjectRoot '.scratch\msys2'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# encoder components: avifenc side (avif) + ultrahdr side (libultrahdr + ANGLE for libuhdr)
$packages = @(
    'libavif-', 'aom-', 'dav1d-', 'zlib-', 'libpng-', 'libjpeg-turbo-',
    'libxml2-[0-9]', 'rav1e-', 'libwebp-', 'svt-av1-', 'libyuv-', 'libiconv-',
    'libgcc-', 'libstdc%2B%2B-', 'winpthreads-git-',
    'libultrahdr-', 'angleproject-'
)

$html = (Invoke-WebRequest -Uri $repo -UseBasicParsing -TimeoutSec 120).Content
foreach ($p in $packages) {
    $latest = [regex]::Matches($html, "mingw-w64-x86_64-$p`w*[0-9a-z\.\-]*any\.pkg\.tar\.zst") |
        ForEach-Object { $_.Value } | Select-Object -Unique | Select-Object -Last 1
    if (-not $latest) { Write-Output "MISSING $p"; continue }
    $out = Join-Path $dir ([uri]::UnescapeDataString($latest))
    if (Test-Path $out) { Write-Output "cached $latest"; continue }
    Write-Output "fetch $latest"
    Invoke-WebRequest -Uri ($repo + $latest) -OutFile $out -UseBasicParsing -TimeoutSec 600
    tar -xf $out -C $dir
}

$msysBin = Join-Path $dir 'mingw64\bin'
$tools = Join-Path $ProjectRoot 'src\hdrshot\bin\Release\net8.0-windows\win-x64\tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Copy-Item (Join-Path $msysBin '*.dll') $tools -Force
foreach ($exe in 'avifenc.exe', 'avifdec.exe', 'ultrahdr_app.exe') {
    Copy-Item (Join-Path $msysBin $exe) $tools -Force
}
if (-not (Test-Path (Join-Path $tools 'libwinpthread-1.dll'))) {
    # newer MSYS2 winpthreads packages no longer ship the DLL; Git for Windows has one
    $gitFallback = 'C:\Program Files\Git\mingw64\bin\libwinpthread-1.dll'
    if (Test-Path $gitFallback) { Copy-Item $gitFallback $tools -Force }
}

Write-Output "=== tools dir ==="
(Get-ChildItem $tools -Name) -join ', '

$required = 'avifenc.exe', 'ultrahdr_app.exe', 'libavif-16.dll', 'libuhdr-1.dll', 'libxml2-16.dll'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $tools $_)) }
if ($missing) {
    Write-Output "WARNING: missing components: $($missing -join ', ')"
} else {
    Write-Output ""
    Write-Output "all encoder components ready: $tools"
}
Write-Output ""
Write-Output "if you have not built yet:  dotnet build src/hdrshot -c Release"
Write-Output "then run:  src\hdrshot\bin\Release\net8.0-windows\win-x64\hdrshot.exe <file.jxr>"
Write-Output "(or skip all of this and download the prebuilt zip from GitHub Releases - tools are bundled)"
