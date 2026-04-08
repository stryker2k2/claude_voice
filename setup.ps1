#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot setup for new developers: downloads Piper + Whisper, then installs
    Claude Voice to %LOCALAPPDATA%\ClaudeVoice\ with a Start Menu shortcut.
.PARAMETER PiperVoice
    Piper voice to download (default: en_US-ryan-high).
    Full list: https://rhasspy.github.io/piper-samples/
.PARAMETER WhisperModel
    Whisper model to download (default: base.en, ~142 MB).
    Options: tiny.en (~75 MB), base.en (~142 MB), small.en (~466 MB)
#>
param(
    [string]$PiperVoice   = "en_US-ryan-high",
    [string]$WhisperModel = "base.en"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ---------------------------------------------------------------------------
# 1. Download Piper TTS binary + voice model
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Step 1: Piper TTS ===" -ForegroundColor Yellow
& "$PSScriptRoot\download-piper.ps1" -Voice $PiperVoice

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
