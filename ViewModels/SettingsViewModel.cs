using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;

namespace claude_voice;

/// <summary>A Piper voice available on disk.</summary>
public record VoiceOption(string DisplayName, string FullPath);

/// <summary>A Whisper model option shown in the Speech Recognition dropdown.</summary>
public record WhisperModelOption(string Key, string DisplayName);

public sealed class SettingsViewModel : ViewModelBase
{
    private string       _apiKey         = "";
    private string       _systemPrompt   = "";
    private VoiceOption? _selectedVoice;
    private string       _pttKey         = "F5";
    private string       _wakeWord       = "hey claude";
    private string       _assistantName  = "Claude";
    private bool         _enableMemory   = true;
    private bool         _enableWebSearch = false;
    private double       _silenceTimeout   = 4.0;
    private double       _voiceThresholdDb = -30.0;
    private string       _wakeSound        = "Quindar";
    private WhisperModelOption? _selectedWhisperModel;

    public string       ApiKey          { get => _apiKey;          set => SetField(ref _apiKey, value); }
    public string       SystemPrompt    { get => _systemPrompt;    set => SetField(ref _systemPrompt, value); }
    public VoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (SetField(ref _selectedVoice, value) && value is not null)
                _previewVoice?.Invoke(value.FullPath);
        }
    }
    public string       PttKey          { get => _pttKey;          set => SetField(ref _pttKey, value); }
    public string       WakeWord        { get => _wakeWord;        set => SetField(ref _wakeWord, value); }
    public string       AssistantName   { get => _assistantName;   set => SetField(ref _assistantName, value); }
    public bool         EnableMemory    { get => _enableMemory;    set => SetField(ref _enableMemory, value); }
    public bool         EnableWebSearch { get => _enableWebSearch; set => SetField(ref _enableWebSearch, value); }
    public double       SilenceTimeout    { get => _silenceTimeout;    set => SetField(ref _silenceTimeout, value); }
    public double       VoiceThresholdDb  { get => _voiceThresholdDb;  set => SetField(ref _voiceThresholdDb, value); }
    public string       WakeSound         { get => _wakeSound;         set => SetField(ref _wakeSound, value); }

    public WhisperModelOption? SelectedWhisperModel
    {
        get => _selectedWhisperModel;
        set => SetField(ref _selectedWhisperModel, value);
    }

    public static IReadOnlyList<WhisperModelOption> WhisperModelOptions { get; } =
    [
        new("base.en", "English only  (base.en, ~142 MB)"),
        new("base",    "Multilingual  (base, ~142 MB)"),
    ];

    public static IReadOnlyList<string> WakeSoundOptions { get; } =
    [
        "Quindar",
        "Chirp",
        "High Tone",
        "Star Trek",
        "R2-D2",
        "MGS Codec",
        "Zelda Chest",
        "Tri-tone",
        "Sonar Ping",
        "Mario Coin",
    ];

    public string ConfigPath { get; } = AppConfig.LoadedPath;

    private bool _isWiped;
    public bool IsWiped { get => _isWiped; private set => SetField(ref _isWiped, value); }

    public ICommand WipeMemoryCommand { get; }

    public IReadOnlyList<VoiceOption> AvailableVoices { get; }

    /// <summary>Model path that was active when the Settings dialog opened — used to restore on Cancel.</summary>
    public string OriginalVoicePath { get; }

    private readonly Action<string>? _previewVoice;

    public SettingsViewModel(
        string apiKey,
        string systemPrompt,
        IReadOnlyList<VoiceOption> voices,
        string currentVoicePath,
        string pttKey,
        string wakeWord,
        string assistantName,
        bool enableMemory,
        bool enableWebSearch,
        double silenceTimeout,
        double voiceThresholdDb,
        string wakeSound,
        string whisperModelKey,
        Action wipeMemoryAction,
        Action<string>? previewVoice = null)
    {
        _apiKey           = apiKey;
        _systemPrompt     = systemPrompt;
        _pttKey           = pttKey;
        _wakeWord         = wakeWord;
        _assistantName    = assistantName;
        _enableMemory     = enableMemory;
        _enableWebSearch  = enableWebSearch;
        _silenceTimeout   = silenceTimeout;
        _voiceThresholdDb = voiceThresholdDb;
        _wakeSound        = WakeSoundOptions.Contains(wakeSound) ? wakeSound : "Quindar";
        _selectedWhisperModel = WhisperModelOptions.FirstOrDefault(o => o.Key == whisperModelKey)
            ?? WhisperModelOptions[0];
        AvailableVoices   = voices;
        OriginalVoicePath = currentVoicePath;
        _previewVoice     = previewVoice;
        WipeMemoryCommand = new RelayCommand(() =>
        {
            if (IsWiped) return;
            wipeMemoryAction();
            IsWiped = true;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); IsWiped = false; };
            timer.Start();
        });

        var currentFile = Path.GetFileName(currentVoicePath);
        _selectedVoice  = voices.FirstOrDefault(v =>
            Path.GetFileName(v.FullPath).Equals(currentFile, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault();
    }
}
