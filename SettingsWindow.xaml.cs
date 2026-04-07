using System.Windows;

namespace claude_voice;

public partial class SettingsWindow : Window
{
    public string SystemPrompt { get; private set; }

    public SettingsWindow(string currentPrompt)
    {
        InitializeComponent();
        SystemPrompt            = currentPrompt;
        SystemPromptBox.Text    = currentPrompt;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SystemPrompt = SystemPromptBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
