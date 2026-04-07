using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace claude_voice;

public partial class MainWindow : Window
{
    private readonly ClaudeService _claude;
    private CancellationTokenSource? _streamCts;

    public MainWindow()
    {
        var config = AppConfig.Load();
        _claude = new ClaudeService(config.AnthropicApiKey);
        InitializeComponent();
    }

    // -------------------------------------------------------------------------
    // PTT

    private void PttButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        PttButton.Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00));
        StatusText.Text = "Recording...";
        // TODO: start STT recording
    }

    private void PttButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        PttButton.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        StatusText.Text = "Ready";
        // TODO: stop recording, transcribe, populate UserInputText
    }

    // -------------------------------------------------------------------------
    // Send

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var userText = UserInputText.Text.Trim();
        if (string.IsNullOrEmpty(userText)) return;

        UserInputText.Clear();
        SetBusy(true);

        _streamCts = new CancellationTokenSource();
        var sb = new StringBuilder();

        try
        {
            await _claude.StreamResponseAsync(
                userText,
                token =>
                {
                    sb.Append(token);
                    Dispatcher.Invoke(() =>
                    {
                        ClaudeResponseText.Text = sb.ToString();
                        ClaudeScrollViewer.ScrollToEnd();
                    });
                },
                _streamCts.Token);

            StatusText.Text = "Ready";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            ClaudeResponseText.Text = $"Error: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
            StatusText.Text = "Error";
        }
        finally
        {
            SetBusy(false);
            _streamCts.Dispose();
            _streamCts = null;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        PttButton.IsEnabled  = !busy;
        StatusText.Foreground = busy
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA))
            : new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
        StatusText.Text = busy ? "Waiting for Claude..." : "Ready";
    }
}
