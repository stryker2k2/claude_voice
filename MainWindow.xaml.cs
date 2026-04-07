using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace claude_voice;

public partial class MainWindow : Window
{
    private readonly ClaudeService _claude;
    private readonly SttService?   _stt;
    private readonly TtsEngine     _tts;
    private CancellationTokenSource? _streamCts;

    public MainWindow()
    {
        var config = AppConfig.Load();
        _claude = new ClaudeService(config.AnthropicApiKey);
        _tts    = new TtsEngine(config);

        // Resolve whisper model path relative to exe, then working directory
        var modelPath = ResolveModelPath(config.WhisperModel);
        try
        {
            _stt = new SttService(modelPath);
        }
        catch (FileNotFoundException ex)
        {
            // App still works for typed input; PTT will be disabled
            MessageBox.Show(ex.Message, "Whisper model not found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        InitializeComponent();

        if (_stt is null)
        {
            PttButton.IsEnabled  = false;
            PttButton.ToolTip    = "Run download-whisper.ps1 to enable PTT";
            StatusText.Text      = "PTT unavailable — model missing";
        }
    }

    // -------------------------------------------------------------------------
    // PTT

    private async void PttButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_stt is null) return;
        PttButton.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA));
        StatusText.Text = "Recording...";
        _stt.StartRecording();
        await Task.CompletedTask;
    }

    private async void PttButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_stt is null || !_stt.IsRecording) return;

        PttButton.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        StatusText.Text = "Transcribing...";

        try
        {
            var text = await _stt.StopAndTranscribeAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                UserInputText.Text   = text;
                UserInputText.CaretIndex = text.Length;
            }
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
            StatusText.Text = $"STT error: {ex.Message}";
        }
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

            // Speak the completed response on a background thread so the UI stays responsive
            var responseText = sb.ToString();
            _ = Task.Run(() => _tts.SpeakAsync(responseText), CancellationToken.None);

            StatusText.Text = "Ready";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            ClaudeResponseText.Text = $"Error: {ex.Message}";
            StatusText.Foreground   = new SolidColorBrush(Colors.OrangeRed);
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
        SendButton.IsEnabled  = !busy;
        PttButton.IsEnabled   = !busy && _stt is not null;
        StatusText.Foreground = busy
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA))
            : new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
        StatusText.Text = busy ? "Waiting for Claude..." : "Ready";
    }

    private static string ResolveModelPath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured))
            return configured;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory,    configured),
            Path.Combine(Directory.GetCurrentDirectory(), configured),
        };

        return candidates.FirstOrDefault(File.Exists) ?? configured;
    }
}
