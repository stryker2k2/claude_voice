using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace claude_voice;

/// <summary>A Piper voice available on disk.</summary>
public record VoiceOption(string DisplayName, string FullPath);

public sealed class SettingsViewModel : ViewModelBase
{
    private string       _systemPrompt   = "";
    private VoiceOption? _selectedVoice;
    private string       _pttKey         = "F5";
    private string       _wakeWord       = "hey claude";
    private string       _assistantName  = "Claude";
    private bool         _enableMemory   = true;
    private double       _silenceTimeout   = 4.0;
    private double       _voiceThresholdDb = -30.0;

    public string       SystemPrompt    { get => _systemPrompt;    set => SetField(ref _systemPrompt, value); }
    public VoiceOption? SelectedVoice   { get => _selectedVoice;   set => SetField(ref _selectedVoice, value); }
    public string       PttKey          { get => _pttKey;          set => SetField(ref _pttKey, value); }
    public string       WakeWord        { get => _wakeWord;        set => SetField(ref _wakeWord, value); }
    public string       AssistantName   { get => _assistantName;   set => SetField(ref _assistantName, value); }
    public bool         EnableMemory    { get => _enableMemory;    set => SetField(ref _enableMemory, value); }
    public double       SilenceTimeout    { get => _silenceTimeout;    set => SetField(ref _silenceTimeout, value); }
    public double       VoiceThresholdDb  { get => _voiceThresholdDb;  set => SetField(ref _voiceThresholdDb, value); }

    public ICommand WipeMemoryCommand { get; }

    public IReadOnlyList<VoiceOption> AvailableVoices { get; }

    public SettingsViewModel(
        string systemPrompt,
        IReadOnlyList<VoiceOption> voices,
        string currentVoicePath,
        string pttKey,
        string wakeWord,
        string assistantName,
        bool enableMemory,
        double silenceTimeout,
        double voiceThresholdDb,
        Action wipeMemoryAction)
    {
        _systemPrompt     = systemPrompt;
        _pttKey           = pttKey;
        _wakeWord         = wakeWord;
        _assistantName    = assistantName;
        _enableMemory     = enableMemory;
        _silenceTimeout   = silenceTimeout;
        _voiceThresholdDb = voiceThresholdDb;
        AvailableVoices = voices;
        WipeMemoryCommand = new RelayCommand(wipeMemoryAction);

        var currentFile = Path.GetFileName(currentVoicePath);
        _selectedVoice  = voices.FirstOrDefault(v =>
            Path.GetFileName(v.FullPath).Equals(currentFile, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault();
    }
}
