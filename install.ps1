# install.ps1
# Publishes Claude Voice and installs it to %LOCALAPPDATA%\ClaudeVoice\
# Creates a Start Menu shortcut.
# Safe to re-run - preserves your existing config.json.

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "ClaudeVoice"
$startMenu  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Claude Voice.lnk"
$exePath    = Join-Path $installDir "claude_voice.exe"

# --- 1. Publish ---
Write-Host "Publishing..."
dotnet publish "$projectDir\claude_voice.csproj" `
    --configuration Release `
    --output $installDir `
    --no-self-contained `
    2>&1 | Where-Object { $_ -match "error|warning|Build" } | Write-Host

if (-not (Test-Path $exePath)) {
    Write-Error "Publish failed - exe not found at $exePath"
    exit 1
}

# --- 2. Copy whisper models ---
$whisperSrc = Join-Path $projectDir "whisper"
$whisperDst = Join-Path $installDir "whisper"
if (Test-Path $whisperSrc) {
    Write-Host "Copying whisper models..."
    Copy-Item $whisperSrc $whisperDst -Recurse -Force
}

# --- 3. Copy piper ---
$piperSrc = Join-Path $projectDir "piper"
$piperDst = Join-Path $installDir "piper"
if (Test-Path $piperSrc) {
    Write-Host "Copying piper..."
    Copy-Item $piperSrc $piperDst -Recurse -Force
} else {
    Write-Warning "Piper not found - TTS will fall back to Windows voice."
}

# --- 4. Copy icon ---
$iconSrc = Join-Path $projectDir "icon.ico"
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc $installDir -Force
}

# --- 5. Seed config.json if not already present (preserve user settings on update) ---
$configDst     = Join-Path $installDir "config.json"
$configSrc     = Join-Path $projectDir "config.json"
$configExample = Join-Path $projectDir "config.example.json"
if (Test-Path $configDst) {
    Write-Host "Preserving existing config.json."
} elseif (Test-Path $configSrc) {
    Write-Host "Copying config.json..."
    Copy-Item $configSrc $configDst
} elseif (Test-Path $configExample) {
    Write-Host "Seeding config.json from config.example.json..."
    Copy-Item $configExample $configDst
    Write-Host ""
    Write-Host "ACTION REQUIRED: Add your Anthropic API key to:" -ForegroundColor Yellow
    Write-Host "  $configDst" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Warning "No config found - create $configDst before launching."
}

# --- 6. Create Start Menu shortcut ---
Write-Host "Creating Start Menu shortcut..."
$shell    = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath       = $exePath
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation     = Join-Path $installDir "icon.ico"
$shortcut.Description      = "Claude Voice - AI voice companion"
$shortcut.Save()

Write-Host ""
Write-Host "Done! Claude Voice installed to: $installDir"
Write-Host "Start Menu shortcut created."
Write-Host ""
Write-Host "To update: just run install.ps1 again. Your config.json will be preserved."
