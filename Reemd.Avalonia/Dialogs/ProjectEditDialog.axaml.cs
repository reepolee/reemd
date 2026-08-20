using System.IO;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Reemd.Models;
using Reemd.Services;

namespace Reemd.Dialogs;

/// <summary>
/// Dialog for editing the list of project shortcuts shown as toolbar buttons.
/// </summary>
public partial class ProjectEditDialog : Window
{
    private readonly List<ProjectShortcut> _projects;

    /// <summary>True while the Name field reflects the path's folder (auto-derived).</summary>
    private bool _nameDerivedFromPath;

    /// <summary>True while we're programmatically setting the name (not a manual edit).</summary>
    private bool _suppressNameDerive;

    /// <summary>True when the fields are being filled for a new entry.</summary>
    private bool _addingNew = true;

    /// <summary>Resulting edited list, valid when the dialog closes with OK.</summary>
    public List<ProjectShortcut> Result { get; }

    /// <summary>Selected global hotkey modifier token (persisted by the caller).</summary>
    public string HotkeyToken => HotkeyCombo.SelectedValue as string ?? ProjectHotkey.DefaultToken;

    // Parameterless ctor required by the Avalonia XAML compiler (never used at runtime).
    public ProjectEditDialog() : this(Array.Empty<ProjectShortcut>(), ProjectHotkey.DefaultToken)
    {
    }

    public ProjectEditDialog(IEnumerable<ProjectShortcut> projects, string projectHotkeyToken)
    {
        InitializeComponent();

        _projects = projects.ToList();
        Result = _projects;

        var terminalOptions = OperatingSystem.IsMacOS()
            ? new[]
            {
                new ComboOption("Terminal (default)", ""),
                new ComboOption("iTerm", "ITerm")
            }
            : new[]
            {
                new ComboOption("Auto (Windows Terminal, fallback cmd)", ""),
                new ComboOption("Windows Terminal", "WindowsTerminal"),
                new ComboOption("cmd", "Cmd"),
                new ComboOption("PowerShell", "PowerShell"),
                new ComboOption("Git Bash", "GitBash")
            };

        TerminalCombo.ItemsSource = terminalOptions;
        TerminalCombo.DisplayMemberBinding = new Binding(nameof(ComboOption.Label));
        TerminalCombo.SelectedValueBinding = new Binding(nameof(ComboOption.Value));
        TerminalCombo.SelectedValue = "";

        HotkeyCombo.ItemsSource = ProjectHotkey.Options
            .Select(o => new ComboOption(o.Label, o.Token))
            .ToArray();
        HotkeyCombo.DisplayMemberBinding = new Binding(nameof(ComboOption.Label));
        HotkeyCombo.SelectedValueBinding = new Binding(nameof(ComboOption.Value));
        HotkeyCombo.SelectedValue = ProjectHotkey.Options.Any(o => o.Token == projectHotkeyToken)
            ? projectHotkeyToken
            : ProjectHotkey.DefaultToken;

        Opened += (_, _) =>
        {
            RefreshList();
            if (ProjectList.ItemCount > 0)
                ProjectList.SelectedIndex = 0;
        };
    }

    private void RefreshList()
    {
        var selected = (ProjectList.SelectedItem as ProjectRow)?.Project;
        var rows = _projects
            .Select((p, i) => new ProjectRow { Number = i + 1, Project = p })
            .ToList();
        ProjectList.ItemsSource = rows;
        if (selected != null)
            ProjectList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Project, selected));
        UpdateMoveButtons();
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if ((ProjectList.SelectedItem as ProjectRow)?.Project is not { } p) return;

        var index = _projects.IndexOf(p);
        if (index <= 0) return;

        _projects.RemoveAt(index);
        _projects.Insert(index - 1, p);
        RefreshList();
        SelectProject(p);
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if ((ProjectList.SelectedItem as ProjectRow)?.Project is not { } p) return;

        var index = _projects.IndexOf(p);
        if (index < 0 || index >= _projects.Count - 1) return;

        _projects.RemoveAt(index);
        _projects.Insert(index + 1, p);
        RefreshList();
        SelectProject(p);
    }

    private void SelectProject(ProjectShortcut p)
    {
        ProjectList.SelectedItem = (ProjectList.ItemsSource as IEnumerable<ProjectRow>)?
            .FirstOrDefault(r => ReferenceEquals(r.Project, p));
    }

    private void UpdateMoveButtons()
    {
        var index = ProjectList.SelectedIndex;
        BtnMoveUp.IsEnabled = index > 0;
        BtnMoveDown.IsEnabled = index >= 0 && index < _projects.Count - 1;
    }

    private void ClearFields()
    {
        _addingNew = true;
        NameTextBox.Text = "";
        PathTextBox.Text = "";
        CommandTextBox.Text = "";
        TerminalCombo.SelectedValue = "";
        NameTextBox.Focus();
    }

    private void ProjectList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ClearWarning();
        if ((ProjectList.SelectedItem as ProjectRow)?.Project is { } p)
        {
            _addingNew = false;
            UpdateMoveButtons();
            NameTextBox.Text = p.Name;
            PathTextBox.Text = p.Path;
            CommandTextBox.Text = p.Command;
            TerminalCombo.SelectedValue = p.Terminal;

            var folder = Path.GetFileName(p.Path.TrimEnd('\\', '/'));
            _nameDerivedFromPath = !string.IsNullOrEmpty(folder)
                && string.Equals(p.Name, folder, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text?.Trim() ?? "";
        var path = PathTextBox.Text?.Trim() ?? "";

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
            Command = CommandTextBox.Text?.Trim() ?? "",
            Terminal = TerminalCombo.SelectedValue as string ?? ""
        });
        ClearWarning();
        RefreshList();
        SelectProject(_projects.Last());
        ClearFields();
    }

    private void Update_Click(object? sender, RoutedEventArgs e)
    {
        if ((ProjectList.SelectedItem as ProjectRow)?.Project is not { } p)
            return;

        var name = NameTextBox.Text?.Trim() ?? "";
        var path = PathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            return;

        if (NameInUse(name, p))
        {
            ShowWarning($"Name already in use: {name}");
            return;
        }

        p.Name = name;
        p.Path = path;
        p.Command = CommandTextBox.Text?.Trim() ?? "";
        p.Terminal = TerminalCombo.SelectedValue as string ?? "";
        ClearWarning();
        RefreshList();
    }

    private bool NameInUse(string name, ProjectShortcut? exclude = null)
    {
        return _projects.Any(p => p != exclude && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowWarning(string message)
    {
        WarningText.Text = "⚠ " + message;
        WarningText.IsVisible = true;
    }

    private void ClearWarning()
    {
        WarningText.IsVisible = false;
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if ((ProjectList.SelectedItem as ProjectRow)?.Project is not { } p)
            return;

        _projects.Remove(p);
        RefreshList();
        ClearFields();
    }

    private void Duplicate_Click(object? sender, RoutedEventArgs e)
    {
        if ((ProjectList.SelectedItem as ProjectRow)?.Project is not { } p)
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
        SelectProject(copy);

        PathTextBox.Focus();
        PathTextBox.SelectionStart = 0;
        PathTextBox.SelectionEnd = PathTextBox.Text?.Length ?? 0;
    }

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

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select project folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                PathTextBox.Text = path;
        }
    }

    private void PathTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        AutoFillNameFromPath();
    }

    private void PathTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        AutoFillNameFromPath();
    }

    private void NameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearWarning();
        if (_suppressNameDerive) return;
        _nameDerivedFromPath = false;
    }

    private void AutoFillNameFromPath()
    {
        var path = PathTextBox.Text?.Trim() ?? "";
        if (path.Length == 0) return;

        var folder = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder)) return;

        if ((NameTextBox.Text?.Trim().Length ?? 0) == 0 || _nameDerivedFromPath)
        {
            _suppressNameDerive = true;
            try { NameTextBox.Text = folder; }
            finally { _suppressNameDerive = false; }
            _nameDerivedFromPath = true;

            var exclude = _addingNew ? null : (ProjectList.SelectedItem as ProjectRow)?.Project;
            if (NameInUse(folder, exclude))
                ShowWarning($"Name already in use: {folder}");
            else
                ClearWarning();
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var duplicate = _projects
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate != null)
        {
            ShowWarning($"Duplicate name in list: {duplicate.Key}");
            SelectProject(duplicate.First());
            return;
        }

        Close(true);
    }
}

/// <summary>Display row for a project in the list, with its 1-based toolbar number.</summary>
public sealed class ProjectRow
{
    public int Number { get; set; }
    public ProjectShortcut Project { get; init; } = new();
}

/// <summary>Simple label/value pair for ComboBoxes.</summary>
public sealed class ComboOption
{
    public string Label { get; }
    public string Value { get; }

    public ComboOption(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public override string ToString() => Label;
}
