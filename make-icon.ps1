# make-icon.ps1
# Converts icon.svg → icon.ico using Inkscape for rendering
# Produces a multi-resolution ICO: 16, 32, 48, 256 px

$ErrorActionPreference = "Stop"

# --- Find Inkscape ---
$inkscape = "C:\Program Files\Inkscape\bin\inkscape.exe"

if (-not (Test-Path $inkscape)) {
    Write-Error "Inkscape not found at $inkscape"
    exit 1
}

$scriptDir = $PSScriptRoot
$svgPath   = Join-Path $scriptDir "icon.svg"
$icoPath   = Join-Path $scriptDir "icon.ico"
$tmpDir    = Join-Path $env:TEMP "claude_voice_icon"

New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

# --- Export each size as PNG ---
$sizes = @(16, 32, 48, 256)
$pngPaths = @{}

foreach ($size in $sizes) {
    $out = Join-Path $tmpDir "icon_${size}.png"
    Write-Host "Exporting ${size}x${size}..."
    $proc = Start-Process -FilePath $inkscape `
        -ArgumentList "--batch-process","--export-type=png","--export-width=$size","--export-height=$size","--export-filename=$out",$svgPath `
        -Wait -PassThru -NoNewWindow
    if (-not (Test-Path $out)) {
        Write-Error "Inkscape failed to export ${size}x${size} (exit code $($proc.ExitCode))"
        exit 1
    }
    $pngPaths[$size] = $out
}

# --- Build ICO binary (PNG-in-ICO, Vista+ compatible) ---
# ICO header: reserved(2) + type=1(2) + count(2)
# Per entry:  width(1) + height(1) + colorCount(1) + reserved(1) +
#             planes(2) + bitCount(2) + bytesInRes(4) + imageOffset(4)

$pngBytes = @{}
foreach ($size in $sizes) {
    $pngBytes[$size] = [System.IO.File]::ReadAllBytes($pngPaths[$size])
}

$headerSize    = 6
$entrySize     = 16
$dataOffset    = $headerSize + $entrySize * $sizes.Count

$ms = New-Object System.IO.MemoryStream

function Write-UInt16($stream, $val) {
    $stream.Write([BitConverter]::GetBytes([uint16]$val), 0, 2)
}
function Write-UInt32($stream, $val) {
    $stream.Write([BitConverter]::GetBytes([uint32]$val), 0, 4)
}

# ICONDIR header
Write-UInt16 $ms 0          # reserved
Write-UInt16 $ms 1          # type: 1 = icon
Write-UInt16 $ms $sizes.Count

# ICONDIRENTRY array
$offset = $dataOffset
foreach ($size in $sizes) {
    $dim  = if ($size -eq 256) { 0 } else { $size }   # 0 means 256 in ICO spec
    $len  = $pngBytes[$size].Length
    $ms.WriteByte([byte]$dim)   # width
    $ms.WriteByte([byte]$dim)   # height
    $ms.WriteByte(0)             # color count (0 = no palette)
    $ms.WriteByte(0)             # reserved
    Write-UInt16 $ms 1           # planes
    Write-UInt16 $ms 32          # bit count
    Write-UInt32 $ms $len        # bytes in resource
    Write-UInt32 $ms $offset     # offset to image data
    $offset += $len
}

# Image data
foreach ($size in $sizes) {
    $ms.Write($pngBytes[$size], 0, $pngBytes[$size].Length)
}

[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$ms.Dispose()

# Cleanup temp files
Remove-Item $tmpDir -Recurse -Force

Write-Host "Done! icon.ico written to: $icoPath"
