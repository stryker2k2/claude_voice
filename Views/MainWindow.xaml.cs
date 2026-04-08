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
    // Settings — opens dialog, hands result back to VM

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsVm = _vm.CreateSettingsViewModel();
        var dlg        = new SettingsWindow(settingsVm) { Owner = this };
        if (dlg.ShowDialog() == true)
            _vm.ApplySettings(settingsVm);
    }
}
