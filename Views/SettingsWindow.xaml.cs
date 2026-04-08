using System.Windows;
using System.Windows.Input;

namespace claude_voice;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)   => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void PttKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Resolve System key (e.g. Alt combos report Key.System with the real key in SystemKey)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Block keys that would break the dialog or are system-reserved
        if (key is Key.Tab or Key.Escape or Key.LWin or Key.RWin)
            return;

        ((SettingsViewModel)DataContext).PttKey = key.ToString();

        // Move focus away so the box stops capturing keys
        PttKeyBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
