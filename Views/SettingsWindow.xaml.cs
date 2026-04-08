using System.Windows;

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
}
