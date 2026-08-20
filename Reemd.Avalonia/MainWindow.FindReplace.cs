using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Reemd;

/// <summary>
/// Partial class containing find and replace bar logic.
/// </summary>
public partial class MainWindow
{
    #region Find Bar

    private void ShowFindBar()
    {
        FindBar.IsVisible = true;
        ReplaceBar.IsVisible = false;
        FindTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        FindTextBox.Focus();
    }

    private void ShowReplaceBar()
    {
        FindBar.IsVisible = true;
        ReplaceBar.IsVisible = true;
        FindTextBox.Text = "";
        ReplaceTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        FindTextBox.Focus();
    }

    private void HideFindBar()
    {
        FindBar.IsVisible = false;
        ReplaceBar.IsVisible = false;
        FindTextBox.Text = "";
        ReplaceTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        Editor.Focus();
    }

    private void DoFind()
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            FindMatchCount.Text = "";
            _findResults.Clear();
            _currentFindIndex = -1;
            Editor.SelectionStart = 0;
            Editor.SelectionEnd = 0;
            return;
        }

        _findResults.Clear();
        var text = Editor.Text ?? string.Empty;
        int index = 0;
        int searchLen = searchText.Length;
        while ((index = text.IndexOf(searchText, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            _findResults.Add(index);
            index += searchLen;
        }

        if (_findResults.Count > 0)
        {
            _currentFindIndex = 0;
            SelectFindMatch(0);
            FindMatchCount.Text = $"1/{_findResults.Count}";
        }
        else
        {
            _currentFindIndex = -1;
            Editor.SelectionStart = 0;
            Editor.SelectionEnd = 0;
            FindMatchCount.Text = "No results";
        }
    }

    private void FindNext()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }
        _currentFindIndex = (_currentFindIndex + 1) % _findResults.Count;
        SelectFindMatch(_currentFindIndex);
        FindMatchCount.Text = $"{_currentFindIndex + 1}/{_findResults.Count}";
        Editor.Focus();
    }

    private void FindPrevious()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }
        _currentFindIndex = (_currentFindIndex - 1 + _findResults.Count) % _findResults.Count;
        SelectFindMatch(_currentFindIndex);
        FindMatchCount.Text = $"{_currentFindIndex + 1}/{_findResults.Count}";
        Editor.Focus();
    }

    private void SelectFindMatch(int matchIndex)
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText) || matchIndex < 0 || matchIndex >= _findResults.Count) return;

        var start = _findResults[matchIndex];
        Editor.SelectionStart = start;
        Editor.SelectionEnd = start + searchText.Length;
        Editor.CaretIndex = start + searchText.Length;

        // Guarantee the match is scrolled into view (including horizontally) —
        // Avalonia only reveals the caret when its index actually changes, so a
        // jump to an already-visible or unchanged position wouldn't scroll.
        EnsureCaretVisible();
    }

    private void FindTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        DoFind();
    }

    private void FindTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
                    FindPrevious();
                else
                    FindNext();
                e.Handled = true;
                break;
            case Key.Escape:
                HideFindBar();
                e.Handled = true;
                break;
        }
    }

    private void FindPrevBtn_Click(object? sender, RoutedEventArgs e) => FindPrevious();
    private void FindNextBtn_Click(object? sender, RoutedEventArgs e) => FindNext();
    private void FindCloseBtn_Click(object? sender, RoutedEventArgs e) => HideFindBar();

    private void DoReplace()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }

        var replaceText = ReplaceTextBox.Text ?? "";
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var currentPos = _findResults[_currentFindIndex];

        ReplaceRange(currentPos, searchText.Length, replaceText);

        DoFind();

        for (int i = 0; i < _findResults.Count; i++)
        {
            if (_findResults[i] >= currentPos + replaceText.Length)
            {
                _currentFindIndex = i;
                SelectFindMatch(i);
                FindMatchCount.Text = $"{i + 1}/{_findResults.Count}";
                return;
            }
        }

        if (_findResults.Count > 0)
        {
            _currentFindIndex = 0;
            SelectFindMatch(0);
            FindMatchCount.Text = $"1/{_findResults.Count}";
        }
    }

    private void ReplaceAll()
    {
        var searchText = FindTextBox.Text;
        var replaceText = ReplaceTextBox.Text ?? "";
        if (string.IsNullOrEmpty(searchText)) return;

        var text = Editor.Text ?? string.Empty;
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(searchText, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            text = text.Remove(index, searchText.Length).Insert(index, replaceText);
            index += replaceText.Length;
            count++;
        }

        if (count > 0)
        {
            ReplaceRange(0, Editor.Text?.Length ?? 0, text);
            Editor.Focus();
            SetStatus($"Replaced {count} occurrence(s)");
        }

        _findResults.Clear();
        _currentFindIndex = -1;
        FindMatchCount.Text = "";
    }

    private void ReplaceTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                DoReplace();
                e.Handled = true;
                break;
            case Key.Escape:
                HideFindBar();
                e.Handled = true;
                break;
        }
    }

    private void ReplaceBtn_Click(object? sender, RoutedEventArgs e) => DoReplace();
    private void ReplaceAllBtn_Click(object? sender, RoutedEventArgs e) => ReplaceAll();
    private void ReplaceCloseBtn_Click(object? sender, RoutedEventArgs e) => HideFindBar();

    #endregion
}
