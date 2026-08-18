using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Reemd;

/// <summary>
/// Partial class containing keyboard and mouse shortcut handlers for the window
/// and editor: font size changes, word wrap, markdown formatting, file navigation, etc.
/// </summary>
public partial class MainWindow
{
    #region Keyboard & Mouse Shortcuts

    private async void BtnPull_Click(object? sender, RoutedEventArgs e)
    {
        await ForcePullAsync();
    }

    private void BtnScrollTop_Click(object? sender, RoutedEventArgs e)
    {
        ScrollEditorToTop();
    }

    private void BtnScrollBottom_Click(object? sender, RoutedEventArgs e)
    {
        ScrollEditorToBottom();
    }

    private void ScrollEditorToTop()
    {
        Editor.CaretIndex = 0;
        if (_editorScrollViewer != null)
            _editorScrollViewer.Offset = new Vector(_editorScrollViewer.Offset.X, 0);
        Dispatcher.UIThread.Post(SyncEditorToPreview, DispatcherPriority.Background);
    }

    private void ScrollEditorToBottom()
    {
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
        if (_editorScrollViewer != null)
            _editorScrollViewer.Offset = new Vector(_editorScrollViewer.Offset.X, ScrollableHeight(_editorScrollViewer));
        Dispatcher.UIThread.Post(SyncEditorToPreview, DispatcherPriority.Background);
    }

    /// <summary>
    /// Fires before the event reaches the focused control.
    /// Ctrl+Scroll over a panel = that panel's font (position-based).
    /// Ctrl+Shift+Scroll = opposite panel's font.
    /// </summary>
    private void Window_PointerWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl+Shift+Scroll -> force the OPPOSITE panel's font
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (IsEditorFocused)
            {
                _previewFontSize = e.Delta.Y > 0
                    ? Math.Min(_previewFontSize + 1, 48)
                    : Math.Max(_previewFontSize - 1, 8);
                ApplyPreviewFontSize();
                SaveSettings();
            }
            else
            {
                _editorFontSize = e.Delta.Y > 0
                    ? Math.Min(_editorFontSize + 1, 48)
                    : Math.Max(_editorFontSize - 1, 8);
                ApplyEditorFontSize();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Scroll -> context-sensitive by mouse position
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            var pos = e.GetPosition(Editor);
            bool overEditor = pos.X >= 0 && pos.Y >= 0 &&
                              pos.X < Editor.Bounds.Width && pos.Y < Editor.Bounds.Height;

            if (overEditor)
            {
                _editorFontSize = e.Delta.Y > 0
                    ? Math.Min(_editorFontSize + 1, 48)
                    : Math.Max(_editorFontSize - 1, 8);
                ApplyEditorFontSize();
            }
            else
            {
                _previewFontSize = e.Delta.Y > 0
                    ? Math.Min(_previewFontSize + 1, 48)
                    : Math.Max(_previewFontSize - 1, 8);
                ApplyPreviewFontSize();
                SaveSettings();
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Window-level (tunneling) handler for keyboard shortcuts — fires before the focused
    /// control (TextBox or WebView). Handles word wrap, force pull, new issue, and font
    /// size shortcuts even when the WebView has focus.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Number keys 1-9 — trigger the matching project shortcut button.
        // Only fires when not typing in a text box, so numbers still type normally.
        if (e.KeyModifiers == KeyModifiers.None)
        {
            var digitIndex = DigitIndex(e.Key);
            if (digitIndex >= 0 && !IsTypingInTextBox())
            {
                LaunchProjectByIndex(digitIndex);
                e.Handled = true;
                return;
            }
        }

        // Alt+Z — toggle word wrap
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0 && e.Key == Key.Z)
        {
            ToggleWordWrap();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+P — force git pull
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
            (e.KeyModifiers & KeyModifiers.Shift) != 0 &&
            e.Key == Key.P)
        {
            _ = ForcePullAsync();
            e.Handled = true;
            return;
        }

        // Ctrl+Plus/Minus/0 (no Shift) — context-sensitive: font of the active panel
        if (e.KeyModifiers == KeyModifiers.Control)
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
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
            (e.KeyModifiers & KeyModifiers.Shift) != 0)
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

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        // Intercept paste so we can handle clipboard images/URLs first.
        if ((e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V) ||
            (e.Key == Key.Insert && (e.KeyModifiers & KeyModifiers.Shift) != 0))
        {
            HandlePaste();
            e.Handled = true;
            return;
        }

        // Alt+Z — toggle word wrap
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0 && e.Key == Key.Z)
        {
            ToggleWordWrap();
            e.Handled = true;
            return;
        }

        // Alt+Up / Alt+Down — move line up/down
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            if (e.Key == Key.Up)
            {
                MoveLineUp();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                MoveLineDown();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+Tab / Ctrl+Shift+Tab — file navigation
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.Tab)
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
                SelectPreviousFile();
            else
                SelectNextFile();
            e.Handled = true;
            Editor.Focus();
            return;
        }

        // Ctrl+Shift+C — insert code block
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
            (e.KeyModifiers & KeyModifiers.Shift) != 0 &&
            e.Key == Key.C)
        {
            InsertCodeBlock();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+I — inline code
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
            (e.KeyModifiers & KeyModifiers.Shift) != 0 &&
            e.Key == Key.I)
        {
            InsertMarkdownWrapper("`");
            e.Handled = true;
            return;
        }

        // F3 / Shift+F3 — find next/previous
        if (e.Key == Key.F3)
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
            return;
        }

        // Markdown formatting and editor shortcuts (exact Ctrl only)
        if (e.KeyModifiers != KeyModifiers.Control) return;

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
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
                    FindPrevious();
                else
                    FindNext();
                e.Handled = true;
                break;
        }
    }

    #endregion
}
