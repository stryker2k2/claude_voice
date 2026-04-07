using System.Windows;
using System.Windows.Input;

namespace claude_voice;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // -------------------------------------------------------------------------
    // PTT

    private void PttButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        PttButton.Background = System.Windows.Media.Brushes.DarkRed;
        StatusText.Text = "Recording...";
        // TODO: start STT recording
    }

    private void PttButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        PttButton.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x3C, 0x3C, 0x3C));
        StatusText.Text = "Ready";
        // TODO: stop recording, transcribe, populate UserInputText
    }

    // -------------------------------------------------------------------------
    // Send

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var text = UserInputText.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // TODO: send text to Claude API, stream response into ClaudeResponseText
        ClaudeResponseText.Text = $"[Sending: {text}]";
        UserInputText.Clear();
        StatusText.Text = "Waiting for Claude...";
    }
}
