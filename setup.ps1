#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot setup for new developers: downloads Piper + Whisper, then installs
    Claude Voice to %LOCALAPPDATA%\ClaudeVoice\ with a Start Menu shortcut.
.PARAMETER WhisperModel
    Whisper model to download (default: base.en, ~142 MB).
    Options: tiny.en (~75 MB), base.en (~142 MB), small.en (~466 MB)
#>
param(
    [string]$WhisperModel = "base.en"
)

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
    "en_GB-jenny_dioco-medium"  # Natural, conversational British female
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ---------------------------------------------------------------------------
# 1. Download Piper TTS binary + voice models
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 1: Piper TTS ===" -ForegroundColor Yellow
foreach ($voice in $PiperVoices) {
    & "$PSScriptRoot\download-piper.ps1" -Voice $voice
}

# ---------------------------------------------------------------------------
# 2. Download Whisper model (for PTT speech-to-text)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 2: Whisper (PTT) ===" -ForegroundColor Yellow
& "$PSScriptRoot\download-whisper.ps1" -Model $WhisperModel

# ---------------------------------------------------------------------------
# 3. Install (publish + copy to %LOCALAPPDATA%\ClaudeVoice\)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 3: Install ===" -ForegroundColor Yellow
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
