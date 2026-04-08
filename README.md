# claude_voice

A Windows WPF desktop app for voice and text conversations with Claude AI.
Uses **Piper TTS** (local neural voice) for spoken responses and **Whisper** (local STT)
for push-to-talk input.

---

## Quick Start (new developer)

```powershell
git clone https://github.com/stryker2k2/claude_voice.git
cd claude_voice

# Downloads Piper + Ryan voice, downloads Whisper base.en, builds and installs
.\setup.ps1
```

Then edit your config and add your API key:

```
%LOCALAPPDATA%\ClaudeVoice\config.json
```

Launch from the **Start Menu** or run `%LOCALAPPDATA%\ClaudeVoice\claude_voice.exe`.

---

## Features

- **Streaming chat** — Claude responses appear word-by-word as they arrive
- **Piper TTS** — local neural voice (Ryan by default); change voices in Settings
- **Push-to-talk** — hold the mic button or F5 to speak; Whisper transcribes locally
- **Dark mode UI** — VS Code-inspired color scheme
- **Settings dialog** — change system prompt and TTS voice on the fly
- **Named-pipe TTS** — optionally pairs with [claude_tts](https://github.com/stryker2k2/claude_tts) for shared voice output

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 21H1+ / Windows 11 | WPF + WinRT |
| .NET 10 SDK | [Download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Anthropic API key | [Get one here](https://console.anthropic.com/) |
| Piper TTS | Downloaded automatically by `setup.ps1` or `download-piper.ps1` |
| Whisper model | Downloaded automatically by `setup.ps1` or `download-whisper.ps1` |

---

## Manual Setup

If you prefer to run steps individually:

### 1. Download Piper (TTS)

```powershell
# Default: Ryan (en_US-ryan-high)
.\download-piper.ps1

# Other voices:
.\download-piper.ps1 -Voice en_GB-cori-high
.\download-piper.ps1 -Voice en_US-amy-low
# Full list: https://rhasspy.github.io/piper-samples/
```

### 2. Download Whisper (PTT speech-to-text)

```powershell
# Default: base.en (~142 MB, good balance of speed and accuracy)
.\download-whisper.ps1

# Faster / smaller:
.\download-whisper.ps1 -Model tiny.en    # ~75 MB
# More accurate / slower:
.\download-whisper.ps1 -Model small.en   # ~466 MB
```

### 3. Install

```powershell
.\install.ps1
```

Publishes to `%LOCALAPPDATA%\ClaudeVoice\` and creates a Start Menu shortcut.
Safe to re-run — your `config.json` is preserved on updates.

---

## Configuration

Edit `config.json` in the install directory (`%LOCALAPPDATA%\ClaudeVoice\config.json`).
A template with all fields is at `config.example.json` in the repo.

```json
{
  "anthropicApiKey": "sk-ant-...",
  "whisperModel":    "whisper\\ggml-base.en.bin",
  "piperExe":        "piper\\piper.exe",
  "piperModel":      "piper\\en_US-ryan-high.onnx",
  "ttsRate":         0.2,
  "ttsVolume":       100,
  "pttKey":          "F5",
  "systemPrompt":    "You are a helpful assistant."
}
```

| Field | Description |
|---|---|
| `anthropicApiKey` | Your Anthropic API key — **required** |
| `whisperModel` | Path to Whisper `.bin` model (relative to exe or absolute) |
| `piperExe` | Path to `piper.exe` |
| `piperModel` | Path to the `.onnx` voice model |
| `ttsRate` | Speaking speed: -10 (slowest) to 10 (fastest) |
| `ttsVolume` | Volume percentage (0–100) |
| `pttKey` | Keyboard key for push-to-talk (default: F5) |
| `systemPrompt` | Claude's system prompt (also editable in Settings dialog) |

---

## Project Structure

```
claude_voice/
├── Models/
│   ├── AppConfig.cs          config.json loader/saver
│   └── ChatMessage.cs        chat message model
├── Services/
│   ├── ClaudeService.cs      Anthropic streaming API client
│   ├── SttService.cs         Whisper.net speech-to-text
│   └── TtsEngine.cs          Piper TTS engine
├── ViewModels/
│   ├── AsyncRelayCommand.cs
│   ├── MainViewModel.cs      main window logic
│   ├── RelayCommand.cs
│   ├── SettingsViewModel.cs
│   └── ViewModelBase.cs
├── Views/
│   ├── MainWindow.xaml       chat UI
│   ├── MainWindow.xaml.cs
│   ├── SettingsWindow.xaml   settings dialog
│   └── SettingsWindow.xaml.cs
├── Themes/
│   └── AppStyles.xaml        shared dark-mode styles
├── config.example.json       safe config template (no secrets)
├── download-piper.ps1        downloads Piper binary + voice model
├── download-whisper.ps1      downloads Whisper GGML model
├── setup.ps1                 one-shot new developer setup
└── install.ps1               build + install to %LOCALAPPDATA%
```

---

## License

MIT — do whatever you want with it.
