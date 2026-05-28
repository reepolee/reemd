using System.Windows;
using System.Windows.Controls;

namespace Reemd;

/// <summary>
/// Partial class containing markdown editing helpers: line movement, markdown
/// formatting wrappers (bold, italic, code, links), and context menu handlers.
/// </summary>
public partial class MainWindow
{
    #region Context Menu Handlers

    private void ContextMenu_Bold_Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdownWrapper("**");
    }

    private void ContextMenu_Italic_Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdownWrapper("*");
    }

    private void ContextMenu_InlineCode_Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdownWrapper("`");
    }

    private void ContextMenu_CodeBlock_Click(object sender, RoutedEventArgs e)
    {
        InsertCodeBlock();
    }

    private void ContextMenu_Link_Click(object sender, RoutedEventArgs e)
    {
        InsertLinkMarkdown();
    }

    #endregion

    #region Line Movement

    /// <summary>
    /// Moves the current line (or selected lines) up one line.
    /// Catches caret at the first line (cannot move up from there).
    /// </summary>
    private void MoveLineUp()
    {
        int caretPos = Editor.CaretIndex;
        int lineIdx = Editor.GetLineIndexFromCharacterIndex(caretPos);

        if (lineIdx <= 0) return;

        string text = Editor.Text;

        int curContentStart = Editor.GetCharacterIndexFromLineIndex(lineIdx);
        int curContentLen = Editor.GetLineLength(lineIdx);
        int prevContentStart = Editor.GetCharacterIndexFromLineIndex(lineIdx - 1);
        int prevContentLen = Editor.GetLineLength(lineIdx - 1);

        // Separator after previous line
        int prevSepStart = prevContentStart + prevContentLen;
        int prevSepLen = curContentStart - prevSepStart;

        // Separator after current line
        int curSepStart = curContentStart + curContentLen;
        int curSepLen;
        if (lineIdx + 1 < Editor.LineCount)
            curSepLen = Editor.GetCharacterIndexFromLineIndex(lineIdx + 1) - curSepStart;
        else
            curSepLen = 0;

        string before = text.Substring(0, prevContentStart);
        string prevContent = text.Substring(prevContentStart, prevContentLen);
        string prevSep = text.Substring(prevSepStart, prevSepLen);
        string curContent = text.Substring(curContentStart, curContentLen);
        string curSep = text.Substring(curSepStart, curSepLen);
        string after = text.Substring(curSepStart + curSepLen);

        // Swap: cur content goes to prev position, prev content moves down
        Editor.Text = before + curContent + prevSep + prevContent + curSep + after;

        // Place cursor at same relative position in the moved line (now one line up)
        int relativeOffset = caretPos - curContentStart;
        Editor.CaretIndex = prevContentStart + relativeOffset;
    }

    /// <summary>
    /// Moves the current line (or selected lines) down one line.
    /// Catches caret at the last line (cannot move down from there).
    /// </summary>
    private void MoveLineDown()
    {
        int caretPos = Editor.CaretIndex;
        int lineIdx = Editor.GetLineIndexFromCharacterIndex(caretPos);
        int totalLines = Editor.LineCount;

        if (lineIdx >= totalLines - 1) return;

        string text = Editor.Text;

        int curContentStart = Editor.GetCharacterIndexFromLineIndex(lineIdx);
        int curContentLen = Editor.GetLineLength(lineIdx);
        int nextContentStart = Editor.GetCharacterIndexFromLineIndex(lineIdx + 1);
        int nextContentLen = Editor.GetLineLength(lineIdx + 1);

        // Separator after current line
        int curSepStart = curContentStart + curContentLen;
        int curSepLen = nextContentStart - curSepStart;

        // Separator after next line
        int nextSepStart = nextContentStart + nextContentLen;
        int nextSepLen;
        if (lineIdx + 2 < totalLines)
            nextSepLen = Editor.GetCharacterIndexFromLineIndex(lineIdx + 2) - nextSepStart;
        else
            nextSepLen = 0;

        string before = text.Substring(0, curContentStart);
        string curContent = text.Substring(curContentStart, curContentLen);
        string curSep = text.Substring(curSepStart, curSepLen);
        string nextContent = text.Substring(nextContentStart, nextContentLen);
        string nextSep = text.Substring(nextSepStart, nextSepLen);
        string after = text.Substring(nextSepStart + nextSepLen);

        // Swap: next content goes to cur position, cur content moves down
        Editor.Text = before + nextContent + curSep + curContent + nextSep + after;

        // Place cursor at same relative position in the moved line (now one line down)
        int relativeOffset = caretPos - curContentStart;
        Editor.CaretIndex = curContentStart + curSep.Length + nextContent.Length + relativeOffset;
    }

    #endregion

    #region Markdown Formatting

    /// <summary>
    /// Wraps the current selection with the given delimiter (e.g. ** for bold, * for italic).
    /// </summary>
    private void InsertMarkdownWrapper(string delimiter)
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionLength;

        if (selLen > 0)
        {
            var selected = Editor.Text.Substring(selStart, selLen);
            Editor.Text = Editor.Text.Remove(selStart, selLen)
                .Insert(selStart, $"{delimiter}{selected}{delimiter}");
            Editor.SelectionStart = selStart;
            Editor.SelectionLength = selLen + delimiter.Length * 2;
        }
        else
        {
            // No selection, insert placeholder
            var placeholder = $"{delimiter}text{delimiter}";
            Editor.Text = Editor.Text.Insert(selStart, placeholder);
            Editor.SelectionStart = selStart + delimiter.Length;
            Editor.SelectionLength = 4; // select "text"
        }

        Editor.Focus();
    }

    /// <summary>
    /// Wraps the selection in a markdown code block (```).
    /// With selection: wraps selected text in ```\n...\n```.
    /// Without selection: inserts a placeholder code block and selects "code".
    /// </summary>
    private void InsertCodeBlock()
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionLength;

        if (selLen > 0)
        {
            var selected = Editor.Text.Substring(selStart, selLen);
            var replacement = $"```\n{selected}\n```";
            Editor.Text = Editor.Text.Remove(selStart, selLen).Insert(selStart, replacement);
            Editor.SelectionStart = selStart;
            Editor.SelectionLength = replacement.Length;
        }
        else
        {
            var replacement = "```\ncode\n```";
            Editor.Text = Editor.Text.Insert(selStart, replacement);
            Editor.SelectionStart = selStart + 4;
            Editor.SelectionLength = 4;
        }

        Editor.Focus();
    }

    /// <summary>
    /// Inserts a markdown link at the cursor position.
    /// </summary>
    private void InsertLinkMarkdown()
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionLength;

        if (selLen > 0)
        {
            var selected = Editor.Text.Substring(selStart, selLen);
            var link = $"[{selected}](url)";
            Editor.Text = Editor.Text.Remove(selStart, selLen).Insert(selStart, link);
            Editor.SelectionStart = selStart + selLen + 3;
            Editor.SelectionLength = 3; // select "url"
        }
        else
        {
            var link = "[link text](url)";
            Editor.Text = Editor.Text.Insert(selStart, link);
            Editor.SelectionStart = selStart + 1;
            Editor.SelectionLength = 9; // select "link text"
        }

        Editor.Focus();
    }

    #endregion
}
