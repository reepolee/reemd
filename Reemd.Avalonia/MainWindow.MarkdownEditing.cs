using Avalonia.Interactivity;

namespace Reemd;

/// <summary>
/// Partial class containing markdown editing helpers: line movement, markdown
/// formatting wrappers (bold, italic, code, links), and context menu handlers.
/// </summary>
public partial class MainWindow
{
    #region Context Menu Handlers

    private void ContextMenu_Cut_Click(object? sender, RoutedEventArgs e) => Editor.Cut();
    private void ContextMenu_Copy_Click(object? sender, RoutedEventArgs e) => Editor.Copy();
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

        Editor.Text = before + curContent + prevSep + prevContent + curSep + after;

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

        Editor.Text = before + nextContent + curSep + curContent + nextSep + after;

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
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionEnd - Editor.SelectionStart;
        var text = Editor.Text ?? string.Empty;

        if (selLen > 0)
        {
            var selected = text.Substring(selStart, selLen);
            Editor.Text = text.Remove(selStart, selLen).Insert(selStart, $"{delimiter}{selected}{delimiter}");
            Editor.SelectionStart = selStart;
            Editor.SelectionEnd = selStart + selLen + delimiter.Length * 2;
        }
        else
        {
            var placeholder = $"{delimiter}text{delimiter}";
            Editor.Text = text.Insert(selStart, placeholder);
            Editor.SelectionStart = selStart + delimiter.Length;
            Editor.SelectionEnd = selStart + delimiter.Length + 4; // select "text"
        }

        Editor.Focus();
    }

    private void InsertCodeBlock()
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionEnd - Editor.SelectionStart;
        var text = Editor.Text ?? string.Empty;

        if (selLen > 0)
        {
            var selected = text.Substring(selStart, selLen);
            var replacement = $"```\n{selected}\n```";
            Editor.Text = text.Remove(selStart, selLen).Insert(selStart, replacement);
            Editor.SelectionStart = selStart;
            Editor.SelectionEnd = selStart + replacement.Length;
        }
        else
        {
            var replacement = "```\ncode\n```";
            Editor.Text = text.Insert(selStart, replacement);
            Editor.SelectionStart = selStart + 4;
            Editor.SelectionEnd = selStart + 8;
        }

        Editor.Focus();
    }

    private void InsertLinkMarkdown()
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionEnd - Editor.SelectionStart;
        var text = Editor.Text ?? string.Empty;

        if (selLen > 0)
        {
            var selected = text.Substring(selStart, selLen);
            var link = $"[{selected}](url)";
            Editor.Text = text.Remove(selStart, selLen).Insert(selStart, link);
            Editor.SelectionStart = selStart + selLen + 3;
            Editor.SelectionEnd = selStart + selLen + 6; // select "url"
        }
        else
        {
            var link = "[link text](url)";
            Editor.Text = text.Insert(selStart, link);
            Editor.SelectionStart = selStart + 1;
            Editor.SelectionEnd = selStart + 10; // select "link text"
        }

        Editor.Focus();
    }

    #endregion
}
