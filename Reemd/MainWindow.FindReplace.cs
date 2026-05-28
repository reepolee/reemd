using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Reemd;

/// <summary>
/// Partial class containing find and replace bar logic.
/// </summary>
public partial class MainWindow
{
    #region Find Bar

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceBar.Visibility = Visibility.Collapsed;
        FindTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        FindTextBox.Focus();
    }

    private void ShowReplaceBar()
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceBar.Visibility = Visibility.Visible;
        FindTextBox.Text = "";
        ReplaceTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        FindTextBox.Focus();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ReplaceBar.Visibility = Visibility.Collapsed;
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
            Editor.Select(0, 0);
            return;
        }

        _findResults.Clear();
        var text = Editor.Text;
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
            Editor.Select(0, 0);
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
        Editor.SelectionLength = searchText.Length;
        Editor.CaretIndex = start + searchText.Length;

        // WPF TextBox auto-scrolls to show the caret when CaretIndex is set
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        DoFind();
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
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

    private void FindPrevBtn_Click(object sender, RoutedEventArgs e)
    {
        FindPrevious();
    }

    private void FindNextBtn_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
    }

    private void FindCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        HideFindBar();
    }

    private void DoReplace()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }

        var replaceText = ReplaceTextBox.Text;
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var currentPos = _findResults[_currentFindIndex];
        var text = Editor.Text;

        // Replace the current match
        Editor.Text = text.Remove(currentPos, searchText.Length).Insert(currentPos, replaceText);

        // Re-run find to refresh positions (text has changed)
        DoFind();

        // If there are still results, the new current match position is shifted.
        // Adjust the index to point to the match right after the replaced text.
        // Since we replaced at currentPos and DoFind resets to index 0,
        // find the first match at or after (currentPos + replaceText.Length)
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

        // No more matches after this position — wrap to first
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
        var replaceText = ReplaceTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var text = Editor.Text;
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
            Editor.Text = text;
            Editor.Focus();
            SetStatus($"Replaced {count} occurrence(s)");
        }

        // Refresh find results
        _findResults.Clear();
        _currentFindIndex = -1;
        FindMatchCount.Text = "";
    }

    private void ReplaceTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
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

    private void ReplaceBtn_Click(object sender, RoutedEventArgs e)
    {
        DoReplace();
    }

    private void ReplaceAllBtn_Click(object sender, RoutedEventArgs e)
    {
        ReplaceAll();
    }

    private void ReplaceCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        HideFindBar();
    }

    #endregion
}
