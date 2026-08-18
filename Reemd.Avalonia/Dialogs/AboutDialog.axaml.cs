using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Reemd.Services;

namespace Reemd.Dialogs;

/// <summary>
/// Branded "About ReeMD" dialog shown from the macOS application menu.
/// </summary>
public partial class AboutDialog : Window
{
    // Parameterless ctor required by the Avalonia XAML compiler (never used at runtime).
    public AboutDialog() : this(false)
    {
    }

    public AboutDialog(bool isDarkMode)
    {
        InitializeComponent();
        VersionText.Text = GetVersion();

        if (isDarkMode)
            ApplyDarkTheme();
    }

    private static string GetVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any SourceLink "+<commit>" suffix.
            var plus = informational.IndexOf('+');
            if (plus >= 0)
                informational = informational[..plus];
            return $"Version {informational}";
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? string.Empty : $"Version {version}";
    }

    private void ApplyDarkTheme()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = new SolidColorBrush(Colors.White);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    private void SourceLink_Tapped(object? sender, TappedEventArgs e)
    {
        ProcessLauncher.OpenWithDefaultApp("https://github.com/reepolee/reemd");
    }
}
