using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace claude_voice;

public sealed class ChatMessage : INotifyPropertyChanged
{
    public string Role        { get; init; } = "";
    public string DisplayName => Role == "user" ? "You" : "Claude";
    public bool   IsUser      => Role == "user";

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
