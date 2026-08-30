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

    private async void ScrollEditorToTop()
    {
        if (_editorScrollViewer != null)
            await AnimateEditorScroll(0);
        Editor.CaretIndex = 0;
        EnsureCaretVisible();
    }

    private async void ScrollEditorToBottom()
    {
        if (_editorScrollViewer != null)
            await AnimateEditorScroll(ScrollableHeight(_editorScrollViewer));
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
        EnsureCaretVisible();
    }

    /// <summary>
    /// Extends the selection from the current caret to the start or end of the
    /// document (Shift+Ctrl+Home/End), keeping the opposite selection end fixed and
    /// scrolling the caret into view at the document edge.
    /// </summary>
    private async void ExtendSelectionToDocumentEdge(bool toStart)
    {
        var caretIndex = Editor.CaretIndex;
        var target = toStart ? 0 : (Editor.Text?.Length ?? 0);

        // The end of the selection opposite the caret stays fixed. Compute it before
        // changing the caret — Avalonia collapses the selection whenever CaretIndex
        // changes, so the range is re-established afterwards (like Shift+PageUp/PageDown).
        var anchor = caretIndex == Editor.SelectionEnd ? Editor.SelectionStart : Editor.SelectionEnd;
        Editor.CaretIndex = target;
        Editor.SelectionStart = Math.Min(anchor, target);
        Editor.SelectionEnd = Math.Max(anchor, target);

        // Scroll to the edge and reveal the caret. Note: unlike ScrollEditorToTop/
        // Bottom, we must NOT reset the caret here or the selection would collapse.
        if (_editorScrollViewer != null)
            await AnimateEditorScroll(toStart ? 0 : ScrollableHeight(_editorScrollViewer));
        EnsureCaretVisible();
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
        // Page the preview when it has focus (the editor handles its own paging in
        // Editor_KeyDown, which only fires when the editor is in the event route).
        if (IsPreviewFocused)
        {
            if (e.KeyModifiers == KeyModifiers.None &&
                (e.Key == Key.PageDown || e.Key == Key.PageUp))
            {
                PagePreview(e.Key == Key.PageDown ? +1 : -1);
                e.Handled = true;
                return;
            }

            // Cmd+Up / Cmd+Down — MacBook paging for the preview.
            if (OperatingSystem.IsMacOS() &&
                (e.KeyModifiers & KeyModifiers.Meta) != 0 &&
                (e.Key == Key.Down || e.Key == Key.Up))
            {
                PagePreview(e.Key == Key.Down ? +1 : -1);
                e.Handled = true;
                return;
            }
        }

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

        var copyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.C && e.KeyModifiers == copyModifiers)
        {
            Editor.Copy();
            _ = PublishClipboardAsync();
            e.Handled = true;
            return;
        }

        // PageDown / PageUp — page the editor by one viewport height.
        // Handled explicitly because Avalonia's built-in TextBox paging is
        // unreliable on macOS for the dedicated Page Up / Page Down keys.
        // Shift+PageUp/PageDown extends the selection (the caret still follows
        // the scroll so it stays visible).
        if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            if (e.Key == Key.PageDown)
            {
                PageEditor(+1, e.KeyModifiers == KeyModifiers.Shift);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.PageUp)
            {
                PageEditor(-1, e.KeyModifiers == KeyModifiers.Shift);
                e.Handled = true;
                return;
            }
        }

        // Cmd+Up / Cmd+Down — page on the MacBook keyboard, which has no dedicated
        // Page keys. Avalonia's macOS backend drops the Fn modifier, so Fn+Up/Down
        // can't be detected; Cmd+arrow is the free, Mac-native alternative.
        // Cmd+Shift+Up/Down extends the selection, like Shift+PageUp/PageDown.
        if (OperatingSystem.IsMacOS() &&
            (e.KeyModifiers & KeyModifiers.Meta) != 0 &&
            (e.Key == Key.Up || e.Key == Key.Down))
        {
            PageEditor(e.Key == Key.Down ? +1 : -1, (e.KeyModifiers & KeyModifiers.Shift) != 0);
            e.Handled = true;
            return;
        }

        // Cmd+Home / Cmd+End — scroll to the document top/bottom with the caret kept
        // visible (the Mac equivalent of Ctrl+Home/End; Avalonia's TextBox has no
        // built-in Cmd+Home/End handling on macOS). Cmd+Shift+Home/End extends the
        // selection to the document edge, like Shift+Ctrl+Home/End — reachable on
        // Mac external keyboards that have Home/End keys.
        if (OperatingSystem.IsMacOS() &&
            (e.KeyModifiers & KeyModifiers.Meta) != 0 &&
            (e.Key == Key.Home || e.Key == Key.End))
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                ExtendSelectionToDocumentEdge(e.Key == Key.Home);
            }
            else if (e.Key == Key.Home)
            {
                ScrollEditorToTop();
            }
            else
            {
                ScrollEditorToBottom();
            }
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

        // Shift+Ctrl+Home / Shift+Ctrl+End — extend the selection to the start/end
        // of the document (the caret stays visible, like Shift+PageUp/PageDown).
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 &&
            (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            if (e.Key == Key.Home)
            {
                ExtendSelectionToDocumentEdge(true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.End)
            {
                ExtendSelectionToDocumentEdge(false);
                e.Handled = true;
                return;
            }
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
