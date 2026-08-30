using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using SukiUI;

namespace Reemd;

/// <summary>
/// Partial class containing theme management: dark/light mode application.
/// </summary>
public partial class MainWindow
{
    #region Theme

    private void BtnToggleTheme_Click(object? sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SetStatus(_isDarkMode ? "Dark theme" : "Light theme");
    }

    /// <summary>
    /// Switches SukiUI's base theme (Dark/Light) and falls back to setting the
    /// window variant directly if the SukiTheme isn't available yet.
    /// </summary>
    private void ApplySukiBaseTheme(bool dark)
    {
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var app = Application.Current;
        if (app == null)
        {
            RequestedThemeVariant = variant;
            return;
        }

        try
        {
            // Switch SukiUI's base theme (this also refreshes its color resources).
            SukiTheme.GetInstance(app).ChangeBaseTheme(variant);
        }
        catch
        {
            // Fall back if SukiTheme isn't registered.
        }

        // Always force our persisted flag to win over the OS theme ("Default"), so a
        // later OS theme change can't desync the themed controls from our colors.
        app.RequestedThemeVariant = variant;
    }

    private void ApplyTheme()
    {
        // SukiUI owns the window background; drive its base theme (and every themed
        // control, including dialogs) from our persisted dark-mode flag.
        ApplySukiBaseTheme(_isDarkMode);

        if (_isDarkMode)
        {
            // Toolbar
            ToolbarBorder.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            ToolbarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));

            // Sidebar
            SidebarBorder.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            SidebarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FileListHeader.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            FileCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            FileListBox.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            FileListBox.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            FileCountStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            SidebarFooter.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

            // Editor
            EditorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            Editor.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            Editor.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));

            // Preview
            PreviewBorder.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            PreviewBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));

            // Grid splitters
            SidebarSplitter.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            EditorPreviewSplitter.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));

            // Find bar
            FindBar.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            FindBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FindTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FindTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            FindTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            FindMatchCount.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

            // Replace bar
            ReplaceBar.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            ReplaceBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            ReplaceTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            ReplaceTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            ReplaceTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

            // Status bar
            AppStatusBar.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            AppStatusBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            StatusText.Foreground = new SolidColorBrush(Colors.White);
            FontSizeText.Foreground = new SolidColorBrush(Colors.White);
            PreviewFontSizeText.Foreground = new SolidColorBrush(Colors.White);
            CursorPositionText.Foreground = new SolidColorBrush(Colors.White);
            GitHubStatusText.Foreground = new SolidColorBrush(Colors.White);
            LastSyncText.Foreground = new SolidColorBrush(Colors.White);
            ClipboardSyncStatusText.Foreground = new SolidColorBrush(Colors.White);

            // Toolbar
            FolderCombo.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FolderCombo.Foreground = new SolidColorBrush(Colors.White);

            BtnToggleTheme.Content = "☀️";
        }
        else
        {
            // Toolbar
            ToolbarBorder.Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));
            ToolbarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));

            // Sidebar
            SidebarBorder.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            SidebarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            FileListHeader.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            FileCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            FileListBox.Background = new SolidColorBrush(Colors.White);
            FileListBox.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            FileCountStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            SidebarFooter.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));

            // Editor
            EditorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Editor.Background = new SolidColorBrush(Colors.White);
            Editor.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            // Preview
            PreviewBorder.Background = new SolidColorBrush(Colors.White);
            PreviewBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

            // Grid splitters
            SidebarSplitter.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            EditorPreviewSplitter.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

            // Find bar
            FindBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            FindBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            FindTextBox.Background = new SolidColorBrush(Colors.White);
            FindTextBox.Foreground = new SolidColorBrush(Colors.Black);
            FindTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
            FindMatchCount.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            // Replace bar
            ReplaceBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            ReplaceBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            ReplaceTextBox.Background = new SolidColorBrush(Colors.White);
            ReplaceTextBox.Foreground = new SolidColorBrush(Colors.Black);
            ReplaceTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));

            // Status bar
            AppStatusBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            AppStatusBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            FontSizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            PreviewFontSizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            CursorPositionText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            GitHubStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            LastSyncText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            ClipboardSyncStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            // Toolbar
            FolderCombo.Background = new SolidColorBrush(Colors.White);
            FolderCombo.Foreground = new SolidColorBrush(Colors.Black);

            BtnToggleTheme.Content = "🌙";
        }

        // Re-render preview with the new theme colors
        if (!string.IsNullOrEmpty(Editor.Text))
        {
            UpdatePreview(Editor.Text, _previewFontSize);
        }
    }

    #endregion
}
