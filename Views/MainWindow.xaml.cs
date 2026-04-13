using System.Windows;
using System.Windows.Input;

namespace claude_voice;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _pttKeyDown;

    public MainWindow()
    {
        _vm         = new MainViewModel();
        DataContext = _vm;

        InitializeComponent();

        _vm.ScrollRequested += (_, _) => ClaudeScrollViewer.ScrollToEnd();
        _vm.WarningRaised   += (_, msg) =>
            MessageBox.Show(msg, "Whisper model not found", MessageBoxButton.OK, MessageBoxImage.Warning);

        Loaded += (_, _) => _vm.RaiseInitialWarnings();
    }

    // -------------------------------------------------------------------------
    // PTT — mouse

    private void PttButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CanUsePtt) _vm.StartPtt();
    }

    private async void PttButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsRecording) await _vm.StopPttAsync();
    }

    // -------------------------------------------------------------------------
    // PTT — keyboard

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != _vm.PttKey || _pttKeyDown || !_vm.CanUsePtt) return;
        _pttKeyDown = true;
        e.Handled   = true;
        _vm.StartPtt();
    }

    private async void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != _vm.PttKey || !_pttKeyDown) return;
        _pttKeyDown = false;
        e.Handled   = true;
        await _vm.StopPttAsync();
    }

    // -------------------------------------------------------------------------
    // Input box — Enter sends, Shift+Enter inserts newline

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null))
            _vm.SendCommand.Execute(null);
    }

    // -------------------------------------------------------------------------
    // Copy message to clipboard

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string text && text.Length > 0)
            System.Windows.Clipboard.SetText(text);
    }

    // -------------------------------------------------------------------------
    // Replay message via TTS

    private void ReplayMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string text && text.Length > 0)
            _vm.ReplaySpeech(text);
    }

    // -------------------------------------------------------------------------
    // Settings — opens dialog, hands result back to VM

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsVm = _vm.CreateSettingsViewModel();
        var dlg        = new SettingsWindow(settingsVm) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.ApplySettings(settingsVm);
        else
            _vm.CancelSettings(settingsVm.OriginalVoicePath);
    }
}
