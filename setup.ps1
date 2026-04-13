#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot setup for new developers: downloads Piper + Whisper, then installs
    Claude Voice to %LOCALAPPDATA%\ClaudeVoice\ with a Start Menu shortcut.
#>
param()

# Voices to download. All will appear in the Settings > Voice dropdown.
# Full list: https://rhasspy.github.io/piper-samples/
$PiperVoices = @(
    # --- Male (US) ---
    "en_US-ryan-high",          # Primary — highest quality US male
    "en_US-joe-medium",         # Alternate US male (warmer, more casual)
    "en_US-arctic-medium",      # Alternate US male (multi-speaker, uses speaker 0)
    "en_US-hfc_male-medium",    # Alternate US male

    # --- Female (US) ---
    "en_US-lessac-high",        # Best quality US female
    "en_US-hfc_female-medium",  # Alternate US female

    # --- Female (GB) ---
    "en_GB-cori-high",          # Best quality British female
    "en_GB-jenny_dioco-medium", # Natural, conversational British female

    # --- Spanish (Latin / Mexico) ---
    "es_MX-ald-medium",         # Male
    "es_MX-claude-high"         # Female
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$piperDir = Join-Path $PSScriptRoot "piper"
New-Item -ItemType Directory -Force -Path $piperDir | Out-Null

# ---------------------------------------------------------------------------
# 1. Piper binary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 1: Piper binary ===" -ForegroundColor Yellow

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
        Write-Error "Could not locate a Windows release asset. Check https://github.com/rhasspy/piper/releases manually."
        exit 1
    }

    Write-Host "    Asset  : $($winAsset.name)"

    $tmpZip = Join-Path $env:TEMP "piper_windows_dl.zip"
    Write-Host "    Downloading binary..."
    Invoke-WebRequest -Uri $winAsset.browser_download_url -OutFile $tmpZip -UseBasicParsing

    Write-Host "    Extracting..."
    $tmpExtract = Join-Path $env:TEMP "piper_extract_$(Get-Random)"
    Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force

    $piperExeInZip = Get-ChildItem -Path $tmpExtract -Recurse -Filter "piper.exe" | Select-Object -First 1
    if (-not $piperExeInZip) {
        Write-Error "piper.exe not found inside the downloaded zip."
        exit 1
    }

    Copy-Item -Path "$($piperExeInZip.DirectoryName)\*" -Destination $piperDir -Recurse -Force
    Remove-Item $tmpExtract -Recurse -Force
    Remove-Item $tmpZip -Force

    Write-Host "    Installed to: $piperDir" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 2. Piper voice models
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 2: Piper voice models ===" -ForegroundColor Yellow

foreach ($voice in $PiperVoices) {
    if ($voice -notmatch '^([a-z]{2})_([A-Z]{2})-(.+)-(.+)$') {
        Write-Warning "Skipping '$voice' — does not match expected pattern 'lang_REGION-name-quality'."
        continue
    }

    $lang    = $Matches[1]
    $region  = $Matches[2]
    $name    = $Matches[3]
    $quality = $Matches[4]

    $hfBase = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0"
    $hfPath = "$hfBase/$lang/${lang}_${region}/$name/$quality"

    foreach ($file in @("$voice.onnx", "$voice.onnx.json")) {
        $dest = Join-Path $piperDir $file
        if (Test-Path $dest) {
            Write-Host "    Skipping $file (already exists)"
        } else {
            Write-Host "    Downloading $file..." -ForegroundColor Cyan
            Invoke-WebRequest -Uri "$hfPath/$file" -OutFile $dest -UseBasicParsing
        }
    }
}

Write-Host "    Voice models ready." -ForegroundColor Green

# ---------------------------------------------------------------------------
# 3. Whisper model
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 3: Whisper (PTT) ===" -ForegroundColor Yellow

# Both models are downloaded so the user can switch between them in Settings.
# base.en  = English-only, slightly more accurate for English (~142 MB)
# base     = Multilingual, supports Spanish and 98 other languages (~142 MB)
$WhisperModels = @("base.en", "base")

$whisperDir = Join-Path $PSScriptRoot "whisper"
New-Item -ItemType Directory -Force -Path $whisperDir | Out-Null
$ProgressPreference = 'SilentlyContinue'

foreach ($model in $WhisperModels) {
    $whisperFile = Join-Path $whisperDir "ggml-$model.bin"
    $whisperUrl  = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$model.bin"

    if (Test-Path $whisperFile) {
        Write-Host "==> Whisper $model model already exists, skipping." -ForegroundColor Green
    } else {
        Write-Host "==> Downloading $model model..." -ForegroundColor Cyan
        try {
            Invoke-WebRequest -Uri $whisperUrl -OutFile $whisperFile -UseBasicParsing
            $sizeMb = [math]::Round((Get-Item $whisperFile).Length / 1MB, 1)
            Write-Host "    Done. ($sizeMb MB)" -ForegroundColor Green
        } catch {
            if (Test-Path $whisperFile) { Remove-Item $whisperFile }
            Write-Error "Whisper download failed for $model`: $_"
            exit 1
        }
    }
}

# ---------------------------------------------------------------------------
# 4. Install
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 4: Install ===" -ForegroundColor Yellow
& "$PSScriptRoot\install.ps1"

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=============================" -ForegroundColor Yellow
Write-Host " Setup complete!" -ForegroundColor Green
Write-Host "=============================" -ForegroundColor Yellow
Write-Host ""
Write-Host "Before launching, edit your config.json:" -ForegroundColor Cyan
Write-Host "  $env:LOCALAPPDATA\ClaudeVoice\config.json"
Write-Host ""
Write-Host "Set your Anthropic API key in that file, then launch from the Start Menu."
Write-Host ""
