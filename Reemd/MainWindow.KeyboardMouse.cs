using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Reemd;

/// <summary>
/// Partial class containing keyboard and mouse shortcut handlers for the window
/// and editor: font size changes, word wrap, markdown formatting, file navigation, etc.
/// </summary>
public partial class MainWindow
{
    #region Keyboard & Mouse Shortcuts

    private async void BtnPull_Click(object sender, RoutedEventArgs e)
    {
        await ForcePullAsync();
    }

    private void BtnScrollTop_Click(object sender, RoutedEventArgs e)
    {
        ScrollEditorToTop();
    }

    private void BtnScrollBottom_Click(object sender, RoutedEventArgs e)
    {
        ScrollEditorToBottom();
    }

    private void ScrollEditorToTop()
    {
        Editor.CaretIndex = 0;
        Editor.ScrollToHome();
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollToTop();
        }
        Dispatcher.BeginInvoke(SyncEditorToPreview, DispatcherPriority.Background);
    }

    private void ScrollEditorToBottom()
    {
        Editor.CaretIndex = Editor.Text.Length;
        Editor.ScrollToEnd();
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollToBottom();
        }
        Dispatcher.BeginInvoke(SyncEditorToPreview, DispatcherPriority.Background);
    }

    /// <summary>
    /// Fires before the event tunnels to the focused/under-mouse control.
    /// Ctrl+Scroll over a panel = that panel's font (position-based).
    /// Ctrl+Shift+Scroll = opposite panel's font.
    /// </summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+Shift+Scroll -> force the OPPOSITE panel's font
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (IsEditorFocused)
            {
                _previewFontSize = e.Delta > 0
                    ? Math.Min(_previewFontSize + 1, 48)
                    : Math.Max(_previewFontSize - 1, 8);
                ApplyPreviewFontSize();
                SaveSettings();
            }
            else
            {
                _editorFontSize = e.Delta > 0
                    ? Math.Min(_editorFontSize + 1, 48)
                    : Math.Max(_editorFontSize - 1, 8);
                ApplyEditorFontSize();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Scroll -> context-sensitive by mouse position
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var pos = Mouse.GetPosition(Editor);
            bool overEditor = pos.X >= 0 && pos.Y >= 0 && pos.X < Editor.ActualWidth && pos.Y < Editor.ActualHeight;

            if (overEditor)
            {
                // Over editor -> change editor font
                _editorFontSize = e.Delta > 0
                    ? Math.Min(_editorFontSize + 1, 48)
                    : Math.Max(_editorFontSize - 1, 8);
                ApplyEditorFontSize();
            }
            else
            {
                // Over preview (or anywhere else) -> change preview font
                _previewFontSize = e.Delta > 0
                    ? Math.Min(_previewFontSize + 1, 48)
                    : Math.Max(_previewFontSize - 1, 8);
                ApplyPreviewFontSize();
                SaveSettings();
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Window-level handler for keyboard shortcuts — fires before tunneling reaches
    /// the focused control (TextBox or WebView2). Handles Alt+Z for word wrap and
    /// Ctrl+Shift+Plus/Minus/0 for preview font size, even when WebView2 has focus.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Alt+Z — toggle word wrap
        // When Alt is held, WPF reports e.Key = Key.System and e.SystemKey = actual key.
        if ((Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) &&
            (e.Key == Key.Z || e.SystemKey == Key.Z))
        {
            ToggleWordWrap();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+P — force git pull
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            e.Key == Key.P)
        {
            _ = ForcePullAsync();
            e.Handled = true;
            return;
        }

        // Ctrl+Plus/Minus/0 (no Shift) — context-sensitive: font of the active panel
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    if (IsEditorFocused)
                    {
                        _editorFontSize = Math.Min(_editorFontSize + 1, 48);
                        ApplyEditorFontSize();
                    }
                    else
                    {
                        _previewFontSize = Math.Min(_previewFontSize + 1, 48);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    if (IsEditorFocused)
                    {
                        _editorFontSize = Math.Max(_editorFontSize - 1, 8);
                        ApplyEditorFontSize();
                    }
                    else
                    {
                        _previewFontSize = Math.Max(_previewFontSize - 1, 8);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    if (IsEditorFocused)
                    {
                        _editorFontSize = 13;
                        ApplyEditorFontSize();
                    }
                    else
                    {
                        _previewFontSize = 14;
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Shift+Plus/Minus/0 — forces the OPPOSITE panel's font
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    if (IsEditorFocused)
                    {
                        _previewFontSize = Math.Min(_previewFontSize + 1, 48);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    else
                    {
                        _editorFontSize = Math.Min(_editorFontSize + 1, 48);
                        ApplyEditorFontSize();
                    }
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    if (IsEditorFocused)
                    {
                        _previewFontSize = Math.Max(_previewFontSize - 1, 8);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    else
                    {
                        _editorFontSize = Math.Max(_editorFontSize - 1, 8);
                        ApplyEditorFontSize();
                    }
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    if (IsEditorFocused)
                    {
                        _previewFontSize = 14;
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    else
                    {
                        _editorFontSize = 13;
                        ApplyEditorFontSize();
                    }
                    e.Handled = true;
                    return;
            }
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+Z — toggle word wrap
        // When Alt is held, WPF reports e.Key = Key.System and e.SystemKey = actual key.
        // Also check e.SystemKey since Alt triggers WM_SYSKEYDOWN.
        if ((Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) &&
            (e.Key == Key.Z || e.SystemKey == Key.Z))
        {
            ToggleWordWrap();
            e.Handled = true;
            return;
        }

        // Alt+Up / Alt+Down — move line up/down
        if ((Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)))
        {
            if (e.Key == Key.Up || e.SystemKey == Key.Up)
            {
                MoveLineUp();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down || e.SystemKey == Key.Down)
            {
                MoveLineDown();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Tab / Ctrl+Shift+Tab — file navigation (needs non-strict modifier check)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Tab)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                SelectPreviousFile();
            else
                SelectNextFile();
            e.Handled = true;
            Editor.Focus();
            return;
        }

        // Ctrl+Shift+C — insert code block (```)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            e.Key == Key.C)
        {
            InsertCodeBlock();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+I — inline code (`)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            e.Key == Key.I)
        {
            InsertMarkdownWrapper("`");
            e.Handled = true;
            return;
        }

        // F3 / Shift+F3 — find next/previous (no Ctrl needed)
        if (e.Key == Key.F3)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
            return;
        }

        // Ctrl+Plus/Minus/0 is handled at Window level (MainWindow_PreviewKeyDown)
        // for context-sensitive behavior. Only other Ctrl-based shortcuts remain here.

        // Markdown formatting and editor shortcuts (exact Ctrl only, no other modifiers)
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.S:
                _ = SaveCurrentFileAsync();
                e.Handled = true;
                break;
            case Key.N:
                CreateNewFile();
                e.Handled = true;
                break;
            case Key.B:
                InsertMarkdownWrapper("**");
                e.Handled = true;
                break;
            case Key.I:
                InsertMarkdownWrapper("*");
                e.Handled = true;
                break;
            case Key.K:
                InsertLinkMarkdown();
                e.Handled = true;
                break;
            case Key.Home:
                ScrollEditorToTop();
                e.Handled = true;
                break;
            case Key.End:
                ScrollEditorToBottom();
                e.Handled = true;
                break;
            case Key.F:
                ShowFindBar();
                e.Handled = true;
                break;
            case Key.H:
                ShowReplaceBar();
                e.Handled = true;
                break;
            case Key.G:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    FindPrevious();
                else
                    FindNext();
                e.Handled = true;
                break;
        }
    }

    #endregion
}
