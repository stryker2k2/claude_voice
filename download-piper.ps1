#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the Piper TTS binary and a voice model into piper\ at the repo root.
.PARAMETER Voice
    The voice model to download (default: en_US-ryan-high).
    Other options: en_US-amy-low, en_US-joe-medium, en_GB-cori-high, etc.
    Full list: https://rhasspy.github.io/piper-samples/
#>
param(
    [string]$Voice = "en_US-ryan-high"
)

$ErrorActionPreference = "Stop"

$piperDir = Join-Path $PSScriptRoot "piper"
New-Item -ItemType Directory -Force -Path $piperDir | Out-Null

# ---------------------------------------------------------------------------
# 1. Piper binary (GitHub latest release) — skipped if already installed
# ---------------------------------------------------------------------------
Write-Host ""
$piperExePath = Join-Path $piperDir "piper.exe"
if (Test-Path $piperExePath) {
    Write-Host "==> Piper binary already installed, skipping." -ForegroundColor Green
} else {
    Write-Host "==> Fetching latest Piper release from GitHub..." -ForegroundColor Cyan

    $releaseApi = "https://api.github.com/repos/rhasspy/piper/releases/latest"
    $headers    = @{ "User-Agent" = "claude_voice-downloader" }

    $release = Invoke-RestMethod -Uri $releaseApi -Headers $headers
    Write-Host "    Version: $($release.tag_name)"

    $winAsset = $release.assets |
        Where-Object { $_.name -match "windows.*(amd64|x86_64)" } |
        Select-Object -First 1

    if (-not $winAsset) {
        $winAsset = $release.assets |
            Where-Object { $_.name -like "*windows*" -and $_.name -like "*.zip" } |
            Select-Object -First 1
    }

    if (-not $winAsset) {
        Write-Error "Could not locate a Windows release asset in $($release.tag_name). Check https://github.com/rhasspy/piper/releases manually."
        exit 1
    }

    Write-Host "    Asset  : $($winAsset.name)"

    $tmpZip = Join-Path $env:TEMP "piper_windows_dl.zip"
    Write-Host "    Downloading binary..."
    Invoke-WebRequest -Uri $winAsset.browser_download_url -OutFile $tmpZip -UseBasicParsing

    Write-Host "    Extracting..."
    $tmpExtract = Join-Path $env:TEMP "piper_extract_$(Get-Random)"
    Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force

    $piperExeInZip = Get-ChildItem -Path $tmpExtract -Recurse -Filter "piper.exe" |
                     Select-Object -First 1
    if (-not $piperExeInZip) {
        Write-Error "piper.exe not found inside the downloaded zip."
        exit 1
    }

    $sourceFolder = $piperExeInZip.DirectoryName
    Copy-Item -Path "$sourceFolder\*" -Destination $piperDir -Recurse -Force
    Remove-Item $tmpExtract -Recurse -Force
    Remove-Item $tmpZip     -Force

    Write-Host "    Installed to: $piperDir" -ForegroundColor Green
} # end if piper binary not present

# ---------------------------------------------------------------------------
# 2. Voice model (Hugging Face — rhasspy/piper-voices)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==> Downloading voice model: $Voice" -ForegroundColor Cyan

if ($Voice -notmatch '^([a-z]{2})_([A-Z]{2})-(.+)-(.+)$') {
    Write-Error "Voice name '$Voice' does not match expected pattern 'lang_REGION-name-quality' (e.g. en_US-ryan-high)."
    exit 1
}

$lang    = $Matches[1]
$region  = $Matches[2]
$name    = $Matches[3]
$quality = $Matches[4]

$hfBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0"
$hfPath = "$hfBase/$lang/${lang}_${region}/$name/$quality"

$onnxFile     = "$Voice.onnx"
$onnxJsonFile = "$Voice.onnx.json"

foreach ($file in @($onnxFile, $onnxJsonFile)) {
    $dest = Join-Path $piperDir $file
    if (Test-Path $dest) {
        Write-Host "    Skipping $file (already exists)"
        continue
    }
    Write-Host "    Downloading $file..."
    Invoke-WebRequest -Uri "$hfPath/$file" -OutFile $dest -UseBasicParsing
}

Write-Host "    Voice model installed." -ForegroundColor Green
Write-Host ""
Write-Host "Done! Piper + voice ready in: $piperDir" -ForegroundColor Yellow
Write-Host ""
