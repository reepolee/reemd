using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Reemd.Dialogs;

/// <summary>Confirms installation of a downloaded ReeMD release.</summary>
public partial class UpdateDialog : Window
{
    public UpdateDialog() : this(string.Empty, false)
    {
    }

    public UpdateDialog(string version, bool isDarkMode)
    {
        InitializeComponent();
        MessageText.Text = $"ReeMD {version} is available. It will download now, then restart after the update is installed.";

        if (isDarkMode)
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            Foreground = new SolidColorBrush(Colors.White);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Install_Click(object? sender, RoutedEventArgs e) => Close(true);
}
