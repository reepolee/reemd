using System.Windows;
using System.Windows.Media;

namespace Reemd;

public partial class MainWindow
{
    #region Status Updates

    /// <summary>
    /// Toggles word wrap on the editor and logs the new state to the status bar.
    /// </summary>
    private void ToggleWordWrap()
    {
        _wordWrapEnabled = !_wordWrapEnabled;
        Editor.TextWrapping = _wordWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SetStatus(_wordWrapEnabled ? "Word wrap: ON" : "Word wrap: OFF");
        SaveSettings();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void UpdateSavedIndicator(bool saved)
    {
        SavedIndicator.Text = saved ? "\U0001f4be Saved" : "\U0001f4be Modified";
        SavedIndicator.Foreground = saved
            ? new SolidColorBrush(_isDarkMode
                ? Color.FromRgb(0x81, 0xC7, 0x84)
                : Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(_isDarkMode
                ? Color.FromRgb(0xEF, 0x9A, 0x9A)
                : Color.FromRgb(0xC6, 0x28, 0x28));
    }

    #endregion
}
