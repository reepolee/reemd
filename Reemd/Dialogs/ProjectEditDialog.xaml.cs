using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Reemd.Models;

namespace Reemd.Dialogs;

/// <summary>
/// Dialog for editing the list of project shortcuts shown as toolbar buttons.
/// Each project has a Name and a folder Path; clicking a button opens VSCode
/// and a terminal in that folder.
/// </summary>
public partial class ProjectEditDialog : Window
{
    private readonly List<ProjectShortcut> _projects;

    /// <summary>True while the Name field reflects the path's folder (auto-derived), so path edits re-derive it.</summary>
    private bool _nameDerivedFromPath;

    /// <summary>True while we're programmatically setting the name (not a manual edit).</summary>
    private bool _suppressNameDerive;

    /// <summary>True when the fields are being filled for a new entry (no project to exclude from name checks).</summary>
    private bool _addingNew = true;

    /// <summary>Resulting edited list, valid when the dialog closes with OK.</summary>
    public List<ProjectShortcut> Result { get; }

    /// <summary>Selected global hotkey modifier token (persisted by the caller).</summary>
    public string HotkeyToken => HotkeyCombo.SelectedValue as string ?? ProjectHotkey.DefaultToken;

    public ProjectEditDialog(IEnumerable<ProjectShortcut> projects, bool isDarkMode, string projectHotkeyToken)
    {
        InitializeComponent();

        _projects = projects.ToList();
        Result = _projects;

        // Display name -> stored token; empty token = Auto
        TerminalCombo.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Auto (Windows Terminal, fallback cmd)", ""),
            new KeyValuePair<string, string>("Windows Terminal", "WindowsTerminal"),
            new KeyValuePair<string, string>("cmd", "Cmd"),
            new KeyValuePair<string, string>("PowerShell", "PowerShell"),
            new KeyValuePair<string, string>("Git Bash", "GitBash")
        };
        TerminalCombo.DisplayMemberPath = "Key";
        TerminalCombo.SelectedValuePath = "Value";
        TerminalCombo.SelectedValue = "";

        // Global hotkey modifier combo — display label -> stored token
        HotkeyCombo.ItemsSource = ProjectHotkey.Options
            .Select(o => new KeyValuePair<string, string>(o.Label, o.Token))
            .ToArray();
        HotkeyCombo.DisplayMemberPath = "Key";
        HotkeyCombo.SelectedValuePath = "Value";
        HotkeyCombo.SelectedValue = ProjectHotkey.Options.Any(o => o.Token == projectHotkeyToken)
            ? projectHotkeyToken
            : ProjectHotkey.DefaultToken;

        if (isDarkMode)
            ApplyDarkTheme();

        Loaded += (_, _) =>
        {
            RefreshList();
            if (ProjectList.Items.Count > 0)
                ProjectList.SelectedIndex = 0;
        };
    }

    private void RefreshList()
    {
        var selected = ProjectList.SelectedItem as ProjectShortcut;
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _projects;
        if (selected != null)
            ProjectList.SelectedItem = selected;
        UpdateMoveButtons();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectShortcut p) return;

        var index = _projects.IndexOf(p);
        if (index <= 0) return;

        _projects.RemoveAt(index);
        _projects.Insert(index - 1, p);
        RefreshList();
        ProjectList.SelectedItem = p;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectShortcut p) return;

        var index = _projects.IndexOf(p);
        if (index < 0 || index >= _projects.Count - 1) return;

        _projects.RemoveAt(index);
        _projects.Insert(index + 1, p);
        RefreshList();
        ProjectList.SelectedItem = p;
    }

    /// <summary>Disables Up at the top and Down at the bottom of the list.</summary>
    private void UpdateMoveButtons()
    {
        var index = ProjectList.SelectedIndex;
        BtnMoveUp.IsEnabled = index > 0;
        BtnMoveDown.IsEnabled = index >= 0 && index < _projects.Count - 1;
    }

    #region Drag-and-Drop Reordering

    private Point _dragStart;
    private ProjectShortcut? _draggedProject;

    private void ProjectList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ProjectList);
        _draggedProject = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ProjectShortcut;
    }

    private void ProjectList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedProject == null) return;

        var pos = e.GetPosition(ProjectList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _ = DragDrop.DoDragDrop(ProjectList, _draggedProject, DragDropEffects.Move);
        _draggedProject = null;
    }

    private void ProjectList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ProjectShortcut)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>
    /// Moves the dragged project to before/after the target item depending on which
    /// half of the target row the mouse is over. Selection follows the dragged item.
    /// </summary>
    private void ProjectList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ProjectShortcut)) is not ProjectShortcut dragged) return;

        var targetItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetItem == null) return;
        var target = targetItem.DataContext as ProjectShortcut;
        if (target == null || ReferenceEquals(target, dragged)) return;

        var oldIndex = _projects.IndexOf(dragged);
        var insertAfter = e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2;

        _projects.RemoveAt(oldIndex);

        var newIndex = _projects.IndexOf(target);
        if (insertAfter) newIndex++;
        newIndex = Math.Min(newIndex, _projects.Count);

        _projects.Insert(newIndex, dragged);
        RefreshList();
        ProjectList.SelectedItem = dragged;
        _draggedProject = null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    #endregion

    private void ClearFields()
    {
        _addingNew = true;
        NameTextBox.Text = "";
        PathTextBox.Text = "";
        CommandTextBox.Text = "";
        TerminalCombo.SelectedValue = "";
        NameTextBox.Focus();
    }

    private void ProjectList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ClearWarning();
        if (ProjectList.SelectedItem is ProjectShortcut p)
        {
            _addingNew = false;
            UpdateMoveButtons();
            NameTextBox.Text = p.Name;
            PathTextBox.Text = p.Path;
            CommandTextBox.Text = p.Command;
            TerminalCombo.SelectedValue = p.Terminal;

            // If the name matches the path's folder, treat it as path-derived so
            // editing the path keeps the name in sync (e.g. after Duplicate).
            var folder = Path.GetFileName(p.Path.TrimEnd('\\', '/'));
            _nameDerivedFromPath = !string.IsNullOrEmpty(folder)
                && string.Equals(p.Name, folder, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var path = PathTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            return;

        if (NameInUse(name))
        {
            ShowWarning($"Name already in use: {name}");
            return;
        }

        _projects.Add(new ProjectShortcut
        {
            Name = name,
            Path = path,
            Command = CommandTextBox.Text.Trim(),
            Terminal = TerminalCombo.SelectedValue as string ?? ""
        });
        ClearWarning();
        RefreshList();
        ProjectList.SelectedItem = _projects.Last();
        ClearFields();
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectShortcut p)
            return;

        var name = NameTextBox.Text.Trim();
        var path = PathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            return;

        if (NameInUse(name, p))
        {
            ShowWarning($"Name already in use: {name}");
            return;
        }

        p.Name = name;
        p.Path = path;
        p.Command = CommandTextBox.Text.Trim();
        p.Terminal = TerminalCombo.SelectedValue as string ?? "";
        ClearWarning();
        RefreshList();
    }

    /// <summary>True when another project (besides <paramref name="exclude"/>) already uses the name.</summary>
    private bool NameInUse(string name, ProjectShortcut? exclude = null)
    {
        return _projects.Any(p => p != exclude && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = "⚠ " + message;
        WarningText.Visibility = Visibility.Visible;
    }

    private void ClearWarning()
    {
        WarningText.Visibility = Visibility.Collapsed;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectShortcut p)
            return;

        _projects.Remove(p);
        RefreshList();
        ClearFields();
    }

    /// <summary>
    /// Duplicates the selected project (path, command, terminal) right after it in the
    /// list with a unique auto-suffixed name, then selects the copy and focuses the
    /// path field so the user can just replace the folder. Duplicates never exist,
    /// not even mid-edit.
    /// </summary>
    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectShortcut p)
            return;

        var index = _projects.IndexOf(p);
        var copy = new ProjectShortcut
        {
            Name = MakeUniqueName(p.Name),
            Path = p.Path,
            Command = p.Command,
            Terminal = p.Terminal
        };

        _projects.Insert(index + 1, copy);
        RefreshList();
        ProjectList.SelectedItem = copy;
        ProjectList.ScrollIntoView(copy);

        PathTextBox.Focus();
        PathTextBox.SelectAll();
    }

    /// <summary>
    /// Returns a name not used by any project, appending -2, -3... to the given base
    /// name until it's free (same convention as the load-time sanitizer).
    /// </summary>
    private string MakeUniqueName(string baseName)
    {
        if (!NameInUse(baseName))
            return baseName;

        var counter = 2;
        string candidate;
        do
        {
            candidate = $"{baseName}-{counter}";
            counter++;
        } while (NameInUse(candidate));

        return candidate;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select project folder",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            PathTextBox.Text = dialog.FolderName;
    }

    /// <summary>
    /// Fires on any path change — typing, paste, drop, or Browse — and keeps the
    /// name in sync with the path's last folder while the name is empty or was
    /// derived from the path (so pasting/typing a path auto-fills the name even
    /// without tabbing out).
    /// </summary>
    private void PathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        AutoFillNameFromPath();
    }

    private void PathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AutoFillNameFromPath();
    }

    /// <summary>
    /// Accepts a folder dropped from Explorer into the path field. Files are
    /// replaced by their containing folder.
    /// </summary>
    private void PathTextBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            var dropped = files[0];
            PathTextBox.Text = Directory.Exists(dropped)
                ? dropped
                : Path.GetDirectoryName(dropped) ?? dropped;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Any name change that isn't our own programmatic auto-fill is a manual edit,
    /// so the name stops following the path.
    /// </summary>
    private void NameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ClearWarning();
        if (_suppressNameDerive) return;
        _nameDerivedFromPath = false;
    }

    /// <summary>
    /// When the path has a value and the name is empty or currently derived from the
    /// path, sets the name to the last folder of the path.
    /// </summary>
    private void AutoFillNameFromPath()
    {
        var path = PathTextBox.Text.Trim();
        if (path.Length == 0) return;

        var folder = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder)) return;

        if (NameTextBox.Text.Trim().Length == 0 || _nameDerivedFromPath)
        {
            _suppressNameDerive = true;
            try { NameTextBox.Text = folder; }
            finally { _suppressNameDerive = false; }
            _nameDerivedFromPath = true;

            // Warn when the derived name is taken by another project — e.g. two
            // projects pointing at the same folder. The project being edited is
            // excluded so editing its own folder doesn't warn against itself.
            var exclude = _addingNew ? null : ProjectList.SelectedItem as ProjectShortcut;
            if (NameInUse(folder, exclude))
                ShowWarning($"Name already in use: {folder}");
            else
                ClearWarning();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var duplicate = _projects
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate != null)
        {
            ShowWarning($"Duplicate name in list: {duplicate.Key}");
            ProjectList.SelectedItem = duplicate.First();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void ApplyDarkTheme()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = new SolidColorBrush(Colors.White);
        ProjectList.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        ProjectList.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        NameTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        NameTextBox.Foreground = new SolidColorBrush(Colors.White);
        PathTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        PathTextBox.Foreground = new SolidColorBrush(Colors.White);
        TerminalCombo.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        TerminalCombo.Foreground = new SolidColorBrush(Colors.White);
        HotkeyCombo.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        HotkeyCombo.Foreground = new SolidColorBrush(Colors.White);
        CommandTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        CommandTextBox.Foreground = new SolidColorBrush(Colors.White);
        WarningText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x9A, 0x9A));
    }
}

/// <summary>
/// Shows the 1-based position of a list row — the toolbar button number the
/// project would get. The value must be the ListBoxItem the row belongs to.
/// </summary>
public sealed class RowIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ListBoxItem item)
        {
            var itemsControl = ItemsControl.ItemsControlFromItemContainer(item);
            var index = itemsControl?.ItemContainerGenerator.IndexFromContainer(item) ?? -1;
            return index >= 0 ? (index + 1).ToString() : "";
        }
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
