using System.ComponentModel;
using Avalonia.Interactivity;

namespace Reemd;

/// <summary>
/// Partial class containing markdown editing helpers: line movement, markdown
/// formatting wrappers (bold, italic, code, links), and context menu handlers.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Replaces the character range [start, start + length) with <paramref name="replacement"/>
    /// through the TextBox text-input pipeline, so the change is recorded for undo/redo.
    /// Assigning Editor.Text directly would clear the undo history instead.
    /// </summary>
    private void ReplaceRange(int start, int length, string replacement)
    {
        Editor.CaretIndex = start;
        Editor.SelectionStart = start;
        Editor.SelectionEnd = start + length;
        Editor.SelectedText = replacement;
    }

    #region Context Menu Handlers

    private void EditorContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        MenuUndo.IsEnabled = Editor.CanUndo;
        MenuRedo.IsEnabled = Editor.CanRedo;
    }

    private void ContextMenu_Undo_Click(object? sender, RoutedEventArgs e) => Editor.Undo();
    private void ContextMenu_Redo_Click(object? sender, RoutedEventArgs e) => Editor.Redo();

    private void ContextMenu_Cut_Click(object? sender, RoutedEventArgs e) => Editor.Cut();
    private void ContextMenu_Copy_Click(object? sender, RoutedEventArgs e)
    {
        Editor.Copy();
        _ = PublishClipboardAsync();
    }
    private void ContextMenu_Paste_Click(object? sender, RoutedEventArgs e) => HandlePaste();

    private void ContextMenu_Bold_Click(object? sender, RoutedEventArgs e) => InsertMarkdownWrapper("**");
    private void ContextMenu_Italic_Click(object? sender, RoutedEventArgs e) => InsertMarkdownWrapper("*");
    private void ContextMenu_InlineCode_Click(object? sender, RoutedEventArgs e) => InsertMarkdownWrapper("`");
    private void ContextMenu_CodeBlock_Click(object? sender, RoutedEventArgs e) => InsertCodeBlock();
    private void ContextMenu_Link_Click(object? sender, RoutedEventArgs e) => InsertLinkMarkdown();

    #endregion

    #region Line Movement

    /// <summary>
    /// Moves the current line (or selected lines) up one line.
    /// </summary>
    private void MoveLineUp()
    {
        var text = Editor.Text ?? string.Empty;
        int caretPos = Editor.CaretIndex;
        int lineIdx = LineIndexFromChar(text, caretPos);

        if (lineIdx <= 0) return;

        int curContentStart = LineStart(text, lineIdx);
        int curContentLen = LineLength(text, lineIdx);
        int prevContentStart = LineStart(text, lineIdx - 1);
        int prevContentLen = LineLength(text, lineIdx - 1);

        int prevSepStart = prevContentStart + prevContentLen;
        int prevSepLen = curContentStart - prevSepStart;

        int curSepStart = curContentStart + curContentLen;
        int curSepLen = lineIdx + 1 < CountLines(text)
            ? LineStart(text, lineIdx + 1) - curSepStart
            : 0;

        string before = text[..prevContentStart];
        string prevContent = text.Substring(prevContentStart, prevContentLen);
        string prevSep = text.Substring(prevSepStart, prevSepLen);
        string curContent = text.Substring(curContentStart, curContentLen);
        string curSep = text.Substring(curSepStart, curSepLen);
        string after = text[(curSepStart + curSepLen)..];

        ReplaceRange(0, text.Length, before + curContent + prevSep + prevContent + curSep + after);

        int relativeOffset = caretPos - curContentStart;
        Editor.CaretIndex = prevContentStart + relativeOffset;
    }

    /// <summary>
    /// Moves the current line (or selected lines) down one line.
    /// </summary>
    private void MoveLineDown()
    {
        var text = Editor.Text ?? string.Empty;
        int caretPos = Editor.CaretIndex;
        int lineIdx = LineIndexFromChar(text, caretPos);
        int totalLines = CountLines(text);

        if (lineIdx >= totalLines - 1) return;

        int curContentStart = LineStart(text, lineIdx);
        int curContentLen = LineLength(text, lineIdx);
        int nextContentStart = LineStart(text, lineIdx + 1);
        int nextContentLen = LineLength(text, lineIdx + 1);

        int curSepStart = curContentStart + curContentLen;
        int curSepLen = nextContentStart - curSepStart;

        int nextSepStart = nextContentStart + nextContentLen;
        int nextSepLen = lineIdx + 2 < totalLines
            ? LineStart(text, lineIdx + 2) - nextSepStart
            : 0;

        string before = text[..curContentStart];
        string curContent = text.Substring(curContentStart, curContentLen);
        string curSep = text.Substring(curSepStart, curSepLen);
        string nextContent = text.Substring(nextContentStart, nextContentLen);
        string nextSep = text.Substring(nextSepStart, nextSepLen);
        string after = text[(nextSepStart + nextSepLen)..];

        ReplaceRange(0, text.Length, before + nextContent + curSep + curContent + nextSep + after);

        int relativeOffset = caretPos - curContentStart;
        Editor.CaretIndex = curContentStart + curSep.Length + nextContent.Length + relativeOffset;
    }

    private static int CountLines(string text) => 1 + text.Count(c => c == '\n');

    private static int LineIndexFromChar(string text, int charIndex)
    {
        int line = 0;
        int limit = Math.Min(charIndex, text.Length);
        for (int i = 0; i < limit; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static int LineStart(string text, int lineIndex)
    {
        if (lineIndex <= 0) return 0;
        int line = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                if (line == lineIndex) return i + 1;
            }
        }
        return text.Length;
    }

    private static int LineLength(string text, int lineIndex)
    {
        int start = LineStart(text, lineIndex);
        int end = start;
        while (end < text.Length && text[end] != '\n') end++;
        if (end > start && text[end - 1] == '\r') return end - start - 1; // strip CRLF \r
        return end - start;
    }

    #endregion

    #region Markdown Formatting

    private void InsertMarkdownWrapper(string delimiter)
    {
        var text = Editor.Text ?? string.Empty;
        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        var selLen = end - start;

        if (selLen > 0)
        {
            var selected = text.Substring(start, selLen);
            var replacement = $"{delimiter}{selected}{delimiter}";
            ReplaceRange(start, selLen, replacement);
            Editor.SelectionStart = start;
            Editor.SelectionEnd = start + replacement.Length;
        }
        else
        {
            var placeholder = $"{delimiter}text{delimiter}";
            ReplaceRange(start, 0, placeholder);
            Editor.SelectionStart = start + delimiter.Length;
            Editor.SelectionEnd = start + delimiter.Length + 4; // select "text"
        }

        Editor.Focus();
    }

    private void InsertCodeBlock()
    {
        var text = Editor.Text ?? string.Empty;
        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        var selLen = end - start;

        if (selLen > 0)
        {
            var selected = text.Substring(start, selLen);
            var replacement = $"```\n{selected}\n```";
            ReplaceRange(start, selLen, replacement);
            Editor.SelectionStart = start;
            Editor.SelectionEnd = start + replacement.Length;
        }
        else
        {
            var replacement = "```\ncode\n```";
            ReplaceRange(start, 0, replacement);
            Editor.SelectionStart = start + 4;
            Editor.SelectionEnd = start + 8;
        }

        Editor.Focus();
    }

    private void InsertLinkMarkdown()
    {
        var text = Editor.Text ?? string.Empty;
        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        var selLen = end - start;

        if (selLen > 0)
        {
            var selected = text.Substring(start, selLen);
            var link = $"[{selected}](url)";
            ReplaceRange(start, selLen, link);
            Editor.SelectionStart = start + selLen + 3;
            Editor.SelectionEnd = start + selLen + 6; // select "url"
        }
        else
        {
            var link = "[link text](url)";
            ReplaceRange(start, 0, link);
            Editor.SelectionStart = start + 1;
            Editor.SelectionEnd = start + 10; // select "link text"
        }

        Editor.Focus();
    }

    #endregion
}
