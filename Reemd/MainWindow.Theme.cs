using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Reemd;

/// <summary>
/// Partial class containing theme management: dark/light mode application and pin button styling.
/// </summary>
public partial class MainWindow
{
    #region Theme

    private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SetStatus(_isDarkMode ? "Dark theme" : "Light theme");
    }

    /// <summary>
    /// Creates an ItemContainerStyle for the file list that styles the pin button
    /// to match the current theme — just sets cursor to hand.
    /// Foreground is NOT set so the 📌 emoji renders in its natural color.
    /// </summary>
    private static Style CreatePinButtonStyle()
    {
        var style = new Style(typeof(ListBoxItem));

        var btnStyle = new Style(typeof(Button));
        btnStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        style.Resources.Add(typeof(Button), btnStyle);

        return style;
    }

    private void ApplyTheme()
    {
        if (_isDarkMode)
        {
            // Dark theme
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            // Sidebar
            SidebarBorder.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            SidebarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FileListHeader.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            FileCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            FileListBox.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            FileListBox.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            FileListBox.ItemContainerStyle = CreatePinButtonStyle();
            FileCountStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            SidebarFooter.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

            // Editor
            EditorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            Editor.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            Editor.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));

            // Preview
            PreviewBorder.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            PreviewBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            Preview.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E);

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

            // Toolbar
            FolderCombo.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FolderCombo.Foreground = new SolidColorBrush(Colors.White);

            BtnToggleTheme.Content = "☀️";
        }
        else
        {
            // Light theme
            Background = SystemColors.WindowBrush;

            // Sidebar
            SidebarBorder.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            SidebarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            FileListHeader.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            FileCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            FileListBox.Background = SystemColors.WindowBrush;
            FileListBox.Foreground = SystemColors.WindowTextBrush;
            FileListBox.ItemContainerStyle = CreatePinButtonStyle();
            FileCountStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            SidebarFooter.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));

            // Editor
            EditorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Editor.Background = SystemColors.WindowBrush;
            Editor.Foreground = SystemColors.WindowTextBrush;

            // Preview
            PreviewBorder.Background = new SolidColorBrush(Colors.White);
            PreviewBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Preview.DefaultBackgroundColor = System.Drawing.Color.White;

            // Grid splitters
            SidebarSplitter.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            EditorPreviewSplitter.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

            // Find bar
            FindBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            FindBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            FindTextBox.Background = new SolidColorBrush(Colors.White);
            FindTextBox.Foreground = SystemColors.WindowTextBrush;
            FindTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
            FindMatchCount.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            // Replace bar
            ReplaceBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            ReplaceBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            ReplaceTextBox.Background = new SolidColorBrush(Colors.White);
            ReplaceTextBox.Foreground = SystemColors.WindowTextBrush;
            ReplaceTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));

            // Status bar
            AppStatusBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            AppStatusBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            StatusText.Foreground = SystemColors.WindowTextBrush;

            FontSizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            PreviewFontSizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            CursorPositionText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            GitHubStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            LastSyncText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            // Toolbar
            FolderCombo.Background = SystemColors.WindowBrush;
            FolderCombo.Foreground = SystemColors.WindowTextBrush;

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
