using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace claude_voice;

/// <summary>A Piper voice available on disk.</summary>
public record VoiceOption(string DisplayName, string FullPath);

public sealed class SettingsViewModel : ViewModelBase
{
    private string      _systemPrompt = "";
    private VoiceOption? _selectedVoice;

    public string      SystemPrompt  { get => _systemPrompt;  set => SetField(ref _systemPrompt, value); }
    public VoiceOption? SelectedVoice { get => _selectedVoice; set => SetField(ref _selectedVoice, value); }

    public IReadOnlyList<VoiceOption> AvailableVoices { get; }

    public SettingsViewModel(
        string systemPrompt,
        IReadOnlyList<VoiceOption> voices,
        string currentVoicePath)
    {
        _systemPrompt  = systemPrompt;
        AvailableVoices = voices;

        var currentFile = Path.GetFileName(currentVoicePath);
        _selectedVoice  = voices.FirstOrDefault(v =>
            Path.GetFileName(v.FullPath).Equals(currentFile, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault();
    }
}
