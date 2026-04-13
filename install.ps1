# install.ps1
# Publishes Claude Voice and installs it to %LOCALAPPDATA%\ClaudeVoice\
# Creates a Start Menu shortcut.
# Always performs a clean install: resets config.json to the example template
# and clears memory.json. Use "dotnet run" during development to keep your config/history.

$ErrorActionPreference = "Stop"

$projectDir = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "ClaudeVoice"
$startMenu  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Claude Voice.lnk"
$exePath    = Join-Path $installDir "claude_voice.exe"
$icoPath    = Join-Path $projectDir "icon.ico"

# --- 0. Generate icon if missing ---
if (-not (Test-Path $icoPath)) {
    $svgPath  = Join-Path $projectDir "icon.svg"
    $inkscape = "C:\Program Files\Inkscape\bin\inkscape.exe"

    if ((Test-Path $svgPath) -and (Test-Path $inkscape)) {
        Write-Host "Generating icon.ico from icon.svg..."

        $tmpDir  = Join-Path $env:TEMP "claude_voice_icon"
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

        $sizes    = @(16, 32, 48, 256)
        $pngBytes = @{}

        foreach ($size in $sizes) {
            $out = Join-Path $tmpDir "icon_${size}.png"
            $proc = Start-Process -FilePath $inkscape `
                -ArgumentList "--batch-process","--export-type=png","--export-width=$size","--export-height=$size","--export-filename=$out",$svgPath `
                -Wait -PassThru -NoNewWindow
            if (-not (Test-Path $out)) {
                Write-Error "Inkscape failed to export ${size}x${size} (exit $($proc.ExitCode))"
                exit 1
            }
            $pngBytes[$size] = [System.IO.File]::ReadAllBytes($out)
        }

        $headerSize = 6
        $entrySize  = 16
        $dataOffset = $headerSize + $entrySize * $sizes.Count
        $ms         = New-Object System.IO.MemoryStream

        function Write-UInt16($s, $v) { $s.Write([BitConverter]::GetBytes([uint16]$v), 0, 2) }
        function Write-UInt32($s, $v) { $s.Write([BitConverter]::GetBytes([uint32]$v), 0, 4) }

        Write-UInt16 $ms 0; Write-UInt16 $ms 1; Write-UInt16 $ms $sizes.Count

        $offset = $dataOffset
        foreach ($size in $sizes) {
            $dim = if ($size -eq 256) { 0 } else { $size }
            $ms.WriteByte([byte]$dim); $ms.WriteByte([byte]$dim)
            $ms.WriteByte(0); $ms.WriteByte(0)
            Write-UInt16 $ms 1; Write-UInt16 $ms 32
            Write-UInt32 $ms $pngBytes[$size].Length
            Write-UInt32 $ms $offset
            $offset += $pngBytes[$size].Length
        }

        foreach ($size in $sizes) { $ms.Write($pngBytes[$size], 0, $pngBytes[$size].Length) }

        [System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
        $ms.Dispose()
        Remove-Item $tmpDir -Recurse -Force

        Write-Host "    icon.ico generated." -ForegroundColor Green
    } else {
        Write-Warning "icon.ico not found and Inkscape is not available - build may fail if the project requires it."
    }
}

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
if (Test-Path $icoPath) {
    Copy-Item $icoPath $installDir -Force
}

# --- 5. Reset config.json from template (fresh install - no preserved settings) ---
$configDst     = Join-Path $installDir "config.json"
$configExample = Join-Path $projectDir "config.example.json"
if (Test-Path $configExample) {
    Write-Host "Writing fresh config.json from config.example.json..."
    Copy-Item $configExample $configDst -Force
    Write-Host ""
    Write-Host "ACTION REQUIRED: Add your Anthropic API key to:" -ForegroundColor Yellow
    Write-Host "  $configDst" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Warning "config.example.json not found - create $configDst before launching."
}

# --- 5b. Clear conversation history ---
$memoryDst = Join-Path $installDir "memory.json"
if (Test-Path $memoryDst) {
    Write-Host "Clearing memory.json..."
    Remove-Item $memoryDst -Force
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
Write-Host "Note: install.ps1 always resets config.json and clears memory. Use 'dotnet run' to keep your config/history."
