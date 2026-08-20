using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Reemd.Dialogs;
using Reemd.Models;
using Reemd.Services;

namespace Reemd;

/// <summary>
/// Partial class for "project shortcut" toolbar buttons. Each project is a named
/// folder; clicking its button (or pressing its number key 1-9) opens the folder
/// in VSCode and starts a terminal in it. The list is editable via the ⚙ button.
/// </summary>
public partial class MainWindow
{
    #region Project Shortcuts

    private readonly List<ProjectShortcut> _projectShortcuts = [];

    /// <summary>Per-repo settings file (<c>.reemd/projects.json</c>) inside the current repo.</summary>
    private string RepoProjectsFilePath => Path.Combine(_markdownFolder, ".reemd", "projects.json");

    /// <summary>Local fallback file, used only for repos that don't have a config yet.</summary>
    private static readonly string LocalProjectsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Reemd", "projects.json");

    /// <summary>Raised after the project list changes, so the app can re-register hotkeys.</summary>
    public event Action? ProjectShortcutsChanged;

    /// <summary>Number of project shortcuts currently configured.</summary>
    public int ProjectShortcutCount => _projectShortcuts.Count;

    /// <summary>Modifier flags for the global project-launch hotkey (Ctrl+Shift+1..9 etc.).</summary>
    public HotKeyModifiers ProjectHotkeyModifiers => ProjectHotkey.ModifiersFor(_projectHotkeyToken);

    private void LoadProjectShortcuts()
    {
        var sanitized = false;
        try
        {
            if (File.Exists(RepoProjectsFilePath))
            {
                // Per-repo config: also carries the hotkey modifier combo.
                var settings = JsonSerializer.Deserialize<ProjectShortcutSettings>(
                    File.ReadAllText(RepoProjectsFilePath));
                if (settings == null) return;

                _projectHotkeyToken = settings.HotkeyToken;
                sanitized = ApplyLoadedProjects(settings.Projects);
            }
            else if (File.Exists(LocalProjectsFilePath))
            {
                // Legacy local file — fallback for repos without a config yet.
                var list = JsonSerializer.Deserialize<List<ProjectShortcut>>(
                    File.ReadAllText(LocalProjectsFilePath));
                if (list == null) return;

                sanitized = ApplyLoadedProjects(list);
            }
            else
            {
                return;
            }
        }
        catch
        {
            // Best-effort — a corrupt projects file just means no buttons
        }

        RebuildProjectButtons();

        if (sanitized)
            SaveProjectShortcuts();

        // The shortcut count and hotkey combo can differ per repo — re-register
        // so the global hotkeys always match the repo that's currently open.
        ProjectShortcutsChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the current shortcut list with the given projects, deriving names
    /// from the path and de-duplicating. Returns true if any names were rewritten.
    /// </summary>
    private bool ApplyLoadedProjects(List<ProjectShortcut> projects)
    {
        _projectShortcuts.Clear();

        var sanitized = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects.Where(p => !string.IsNullOrWhiteSpace(p.Path)))
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                var folder = Path.GetFileName(p.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folder))
                    continue;
                p.Name = folder;
                sanitized = true;
            }

            if (seen.Contains(p.Name))
            {
                var counter = 2;
                string candidate;
                do
                {
                    candidate = $"{p.Name}-{counter}";
                    counter++;
                } while (seen.Contains(candidate));
                p.Name = candidate;
                sanitized = true;
            }

            seen.Add(p.Name);
            _projectShortcuts.Add(p);
        }

        return sanitized;
    }

    private void SaveProjectShortcuts()
    {
        try
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

            // Per-repo copy (.reemd/projects.json in the current repo) is the source
            // of truth — it travels with the repo, so opening another repo shows
            // that repo's shortcuts automatically (and git sync commits it).
            if (Path.IsPathRooted(_markdownFolder) && Directory.Exists(_markdownFolder))
            {
                var repoDir = Path.Combine(_markdownFolder, ".reemd");
                Directory.CreateDirectory(repoDir);
                File.WriteAllText(Path.Combine(repoDir, "projects.json"),
                    JsonSerializer.Serialize(new ProjectShortcutSettings
                    {
                        HotkeyToken = _projectHotkeyToken,
                        Projects = _projectShortcuts
                    }, jsonOptions));
            }

            // Local copy as a fallback for repos that don't have a config yet.
            var dir = Path.GetDirectoryName(LocalProjectsFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(LocalProjectsFilePath,
                JsonSerializer.Serialize(_projectShortcuts, jsonOptions));
        }
        catch
        {
            // Best-effort
        }
    }

    /// <summary>Rebuilds the toolbar buttons from the current project list.</summary>
    private void RebuildProjectButtons()
    {
        var hotkeyLabel = ProjectHotkey.LabelForToken(_projectHotkeyToken);
        ProjectButtonsHost.ItemsSource = _projectShortcuts
            .Select((p, i) => new ProjectButtonItem { Number = i + 1, Project = p, HotkeyLabel = hotkeyLabel })
            .ToList();
    }

    private void ProjectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectButtonItem item })
            LaunchProject(item.Project);
    }

    /// <summary>
    /// Opens the edit dialog for the project list, then persists any changes
    /// and rebuilds the toolbar buttons.
    /// </summary>
    private async void BtnEditProjects_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ProjectEditDialog(_projectShortcuts, _projectHotkeyToken);

        if (await dialog.ShowDialog<bool>(this))
        {
            _projectShortcuts.Clear();
            _projectShortcuts.AddRange(dialog.Result);
            _projectHotkeyToken = dialog.HotkeyToken;
            SaveProjectShortcuts();
            SaveSettings();
            RebuildProjectButtons();
            SetStatus($"{_projectShortcuts.Count} project shortcut(s)");
            ProjectShortcutsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Number-key hotkey: 1-9 triggers the matching project button.
    /// </summary>
    private void HandleProjectHotKey(Key key)
    {
        int index = DigitIndex(key);
        if (index < 0) return;
        LaunchProjectByIndex(index);
    }

    /// <summary>Returns the 0-based index for digit keys 1-9, or -1 if not a digit.</summary>
    private static int DigitIndex(Key key)
    {
        return key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            _ => -1
        };
    }

    /// <summary>
    /// Launches the project at the given 0-based index (bounds-checked).
    /// </summary>
    public void LaunchProjectByIndex(int index)
    {
        if (index < 0 || index >= _projectShortcuts.Count) return;
        LaunchProject(_projectShortcuts[index]);
    }

    private void LaunchProject(ProjectShortcut project)
    {
        var path = project.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            SetStatus($"Project folder not found: {path}");
            return;
        }

        try
        {
            var command = project.Command?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                ProcessLauncher.LaunchVSCode(path);
                ProcessLauncher.LaunchTerminal(path, project.Terminal);
                SetStatus($"Opened {project.Name}: VSCode + {ProcessLauncher.TerminalDisplayName(project.Terminal)}");
            }
            else
            {
                ProcessLauncher.RunCustomCommand(command, path, project.Name);
                SetStatus($"Ran command for {project.Name}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error opening {project.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the focused element is a text input (editor, find/replace box,
    /// editable folder combo), so number hotkeys should NOT fire while typing.
    /// </summary>
    private bool IsTypingInTextBox()
    {
        return TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
    }

    /// <summary>Display wrapper for a project toolbar button, showing its hotkey number.</summary>
    public sealed class ProjectButtonItem
    {
        public int Number { get; init; }
        public ProjectShortcut Project { get; init; } = new();
        public string HotkeyLabel { get; init; } = "Ctrl+Shift";

        /// <summary>📁 for the default VSCode + terminal launch, 🛠️ for custom commands.</summary>
        public string Icon => string.IsNullOrWhiteSpace(Project.Command) ? "📁" : "🛠️";
        public string Label => $"{Icon} {Number} {Project.Name}";
        public string ToolTip => string.IsNullOrWhiteSpace(Project.Command)
            ? $"Open in VSCode + {ProcessLauncher.TerminalDisplayName(Project.Terminal)} (press {Number}, or {HotkeyLabel}+{Number} anywhere)"
            : $"Run: {Project.Command} (press {Number}, or {HotkeyLabel}+{Number} anywhere)";
    }

    #endregion
}
