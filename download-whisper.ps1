# ---------------------------------------------------------------------------
# download-whisper.ps1
# Downloads a Whisper GGML model from Hugging Face into .\whisper\
#
# Usage:
#   .\download-whisper.ps1                      -- downloads base.en (default, ~142 MB)
#   .\download-whisper.ps1 -Model tiny.en       -- tiny.en  (~75 MB,  fastest)
#   .\download-whisper.ps1 -Model small.en      -- small.en (~466 MB, better accuracy)
#   .\download-whisper.ps1 -Model medium.en     -- medium.en (~1.5 GB, high accuracy)
# ---------------------------------------------------------------------------

param(
    [string]$Model = "base.en"
)

$outputDir  = Join-Path $PSScriptRoot "whisper"
$outputFile = Join-Path $outputDir "ggml-$Model.bin"
$url        = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$Model.bin"

if (Test-Path $outputFile) {
    Write-Host "Model already exists: $outputFile"
    exit 0
}

if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Write-Host "Downloading $Model model..."
Write-Host "  From : $url"
Write-Host "  To   : $outputFile"
Write-Host ""

try {
    $ProgressPreference = 'SilentlyContinue'  # much faster without progress bar
    Invoke-WebRequest -Uri $url -OutFile $outputFile -UseBasicParsing
    $sizeMb = [math]::Round((Get-Item $outputFile).Length / 1MB, 1)
    Write-Host "Done. ($sizeMb MB)"
    Write-Host ""
    Write-Host "Update config.json with:"
    Write-Host "  `"whisperModel`": `"whisper\\ggml-$Model.bin`""
} catch {
    Write-Error "Download failed: $_"
    if (Test-Path $outputFile) { Remove-Item $outputFile }
    exit 1
}
