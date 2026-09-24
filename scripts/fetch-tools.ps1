param([string]$ProjectRoot = "")
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

function Fetch([string]$url, [string]$outFile) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -TimeoutSec 600
            return $true
        } catch {
            Write-Output "  download attempt $attempt failed: $($_.Exception.Message)"
            if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }  # drop partial file
            if ($attempt -lt 3) { Start-Sleep (2 * $attempt) }
        }
    }
    return $false
}

$html = (Invoke-WebRequest -Uri $repo -UseBasicParsing -TimeoutSec 120).Content
$failed = @()
foreach ($p in $packages) {
    $latest = [regex]::Matches($html, "mingw-w64-x86_64-$p`w*[0-9a-z\.\-]*any\.pkg\.tar\.zst") |
        ForEach-Object { $_.Value } | Select-Object -Unique | Select-Object -Last 1
    if (-not $latest) { Write-Output "MISSING $p"; $failed += $p; continue }
    $out = Join-Path $dir ([uri]::UnescapeDataString($latest))
    if (Test-Path $out) { Write-Output "cached $latest"; continue }
    Write-Output "fetch $latest"
    if (-not (Fetch ($repo + $latest) $out)) { $failed += $p; continue }
    try { tar -xf $out -C $dir } catch { Write-Output "  extract failed: $($_.Exception.Message)"; $failed += $p }
}

$msysBin = Join-Path $dir 'mingw64\bin'
$tools = Join-Path $ProjectRoot 'src\hdrshot\bin\Release\net8.0-windows\win-x64\tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
if (Test-Path (Join-Path $msysBin '*.dll')) { Copy-Item (Join-Path $msysBin '*.dll') $tools -Force }
foreach ($exe in 'avifenc.exe', 'avifdec.exe', 'ultrahdr_app.exe') {
    $src = Join-Path $msysBin $exe
    if (Test-Path $src) { Copy-Item $src $tools -Force }
}

# GCC runtime fallback: newer MSYS2 winpthreads/libstdc++ packages sometimes fail to download;
# Git for Windows ships compatible copies of all three
$gitBin = 'C:\Program Files\Git\mingw64\bin'
foreach ($dll in 'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll') {
    if (-not (Test-Path (Join-Path $tools $dll)) -and (Test-Path (Join-Path $gitBin $dll))) {
        Copy-Item (Join-Path $gitBin $dll) $tools -Force
        Write-Output "fallback (from Git for Windows): $dll"
    }
}

Write-Output "=== tools dir ==="
(Get-ChildItem $tools -Name) -join ', '

$required = 'avifenc.exe', 'ultrahdr_app.exe', 'libavif-16.dll', 'libuhdr-1.dll', 'libxml2-16.dll',
            'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $tools $_)) }
if ($missing) {
    Write-Output ""
    Write-Output "WARNING: still missing: $($missing -join ', ')"
    if ($failed) { Write-Output "downloads that failed (just re-run this script, cached packages are skipped): $($failed -join ', ')" }
} else {
    Write-Output ""
    Write-Output "all encoder components ready: $tools"
}
Write-Output ""
Write-Output "if you have not built yet:  dotnet build src/hdrshot -c Release"
Write-Output "then run:  src\hdrshot\bin\Release\net8.0-windows\win-x64\hdrshot.exe <file.jxr>"
Write-Output "(or skip all of this and download the prebuilt zip from GitHub Releases - tools are bundled)"
