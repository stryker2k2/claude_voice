using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace claude_voice;

public sealed class ChatMessage : INotifyPropertyChanged
{
    public string Role        { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool   IsHistorical { get; init; } = false;
    public bool   IsUser      => Role == "user";
    public bool   IsSystem    => Role == "system";
    public bool   IsAssistant => Role == "assistant";

    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
