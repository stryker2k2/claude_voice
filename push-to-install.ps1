#Requires -Version 5.1
<#
.SYNOPSIS
    Copies config.json and memory.json from the dev environment to the installed
    ClaudeVoice app in %LOCALAPPDATA%\ClaudeVoice\ so you can pick up your
    conversation where you left off in the installed version.
#>

$dest = Join-Path $env:LOCALAPPDATA "ClaudeVoice"

if (-not (Test-Path $dest)) {
    Write-Error "Installed app not found at: $dest - Run install.ps1 first."
    exit 1
}

$copied = 0
$root = Split-Path $MyInvocation.MyCommand.Path

# --- config.json ---
$configSrc = Join-Path $root "config.json"
if (Test-Path $configSrc) {
    Copy-Item $configSrc (Join-Path $dest "config.json") -Force
    Write-Host "  config.json  ->  $dest" -ForegroundColor Green
    $copied++
} else {
    Write-Warning "config.json not found at $configSrc - skipping."
}

# --- memory.json (written by dotnet run into the build output dir) ---
$binDebug  = Join-Path $root "bin\Debug"
$memorySrc = Get-ChildItem $binDebug -Recurse -Filter "memory.json" -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending |
             Select-Object -First 1

if ($memorySrc) {
    Copy-Item $memorySrc.FullName (Join-Path $dest "memory.json") -Force
    Write-Host "  memory.json  ->  $dest" -ForegroundColor Green
    $copied++
} else {
    Write-Warning "memory.json not found under $binDebug - has the app been run at least once with memory enabled?"
}

Write-Host ""
if ($copied -gt 0) {
    Write-Host "Done. $copied file(s) pushed to the installed app." -ForegroundColor Cyan
} else {
    Write-Host "Nothing was copied." -ForegroundColor Yellow
}
