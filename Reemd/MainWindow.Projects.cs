using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Reemd.Dialogs;
using Reemd.Models;

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

    private static readonly string ProjectsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Reemd", "projects.json");

    /// <summary>Raised after the project list changes, so the app can re-register hotkeys.</summary>
    public event Action? ProjectShortcutsChanged;

    /// <summary>Number of project shortcuts currently configured.</summary>
    public int ProjectShortcutCount => _projectShortcuts.Count;

    /// <summary>Win32 modifier flags for the global project-launch hotkey (Ctrl+Shift+1..9 etc.).</summary>
    public uint ProjectHotkeyModifiers => ProjectHotkey.ModifiersFor(_projectHotkeyToken);

    private void LoadProjectShortcuts()
    {
        var sanitized = false;
        try
        {
            if (!File.Exists(ProjectsFilePath)) return;

            var json = File.ReadAllText(ProjectsFilePath);
            var list = JsonSerializer.Deserialize<List<ProjectShortcut>>(json);
            if (list == null) return;

            _projectShortcuts.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list.Where(p => !string.IsNullOrWhiteSpace(p.Path)))
            {
                // Empty names get derived from the last folder of the path (same
                // convention as the edit dialog); entries with no derivable name
                // are dropped.
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    var folder = Path.GetFileName(p.Path.TrimEnd('\\', '/'));
                    if (string.IsNullOrWhiteSpace(folder))
                        continue;
                    p.Name = folder;
                    sanitized = true;
                }

                // Rename duplicates (case-insensitive) with a -2, -3... suffix
                // instead of dropping them.
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
        }
        catch
        {
            // Best-effort — a corrupt projects file just means no buttons
        }

        RebuildProjectButtons();

        // Persist the sanitized list so the fix is permanent
        if (sanitized)
            SaveProjectShortcuts();
    }

    private void SaveProjectShortcuts()
    {
        try
        {
            var dir = Path.GetDirectoryName(ProjectsFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(ProjectsFilePath,
                JsonSerializer.Serialize(_projectShortcuts, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort — settings save should never crash the app
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

    private void ProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectButtonItem item })
            LaunchProject(item.Project);
    }

    /// <summary>
    /// Opens the edit dialog for the project list, then persists any changes
    /// and rebuilds the toolbar buttons.
    /// </summary>
    private void BtnEditProjects_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProjectEditDialog(_projectShortcuts, _isDarkMode, _projectHotkeyToken)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
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
    /// Number-key hotkey: 1-9 triggers the matching project button. Only fires when
    /// the user isn't typing in a text box, so numbers still type normally in the editor.
    /// </summary>
    private void HandleProjectHotKey(Key key)
    {
        int index = key switch
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

        if (index < 0) return;

        LaunchProjectByIndex(index);
    }

    /// <summary>
    /// Launches the project at the given 0-based index (bounds-checked). Used both
    /// by the in-window number keys and the global Ctrl+Shift+1..9 hotkeys.
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
                LaunchVSCode(path);
                LaunchTerminal(path, project.Terminal);
                SetStatus($"Opened {project.Name}: VSCode + {TerminalDisplayName(project.Terminal)}");
            }
            else
            {
                RunCustomCommand(command, path, project.Name);
                SetStatus($"Ran command for {project.Name}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error opening {project.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a custom command via cmd.exe with the project folder as working directory.
    /// {path} is replaced with the quoted folder path (handles both {path} and "{path}"),
    /// {name} with the project name. Supports && chaining like cmd itself.
    /// </summary>
    private void RunCustomCommand(string command, string path, string name)
    {
        command = command
            .Replace("\"{path}\"", $"\"{path}\"", StringComparison.OrdinalIgnoreCase)
            .Replace("{path}", $"\"{path}\"", StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            WorkingDirectory = path,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// Opens the folder in VSCode. Tries the `code` CLI first (resolved via the
    /// shell so code.cmd is found), then falls back to common install paths.
    /// </summary>
    private void LaunchVSCode(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            return;
        }
        catch
        {
            // Fall through to known install locations
        }

        var candidates = new[]
        {
            @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
            @"%ProgramFiles%\Microsoft VS Code\Code.exe",
            @"%ProgramFiles(x86)%\Microsoft VS Code\Code.exe"
        };

        foreach (var candidate in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = expanded,
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }
        }

        throw new InvalidOperationException("VSCode not found ('code' not on PATH)");
    }

    /// <summary>
    /// Opens a terminal in the folder according to the project's Terminal setting.
    /// Empty/unknown = Auto: Windows Terminal (wt.exe), falling back to cmd.
    /// </summary>
    private static void LaunchTerminal(string path, string? terminal)
    {
        switch (terminal)
        {
            case "WindowsTerminal":
                LaunchWindowsTerminal(path);
                break;
            case "Cmd":
                LaunchCmdTerminal(path);
                break;
            case "PowerShell":
                LaunchPowerShell(path);
                break;
            case "GitBash":
                LaunchGitBash(path);
                break;
            default:
                // Auto — Windows Terminal first, classic cmd as fallback
                try
                {
                    LaunchWindowsTerminal(path);
                }
                catch
                {
                    LaunchCmdTerminal(path);
                }
                break;
        }
    }

    private static void LaunchWindowsTerminal(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "wt.exe",
            Arguments = $"-d \"{path}\"",
            UseShellExecute = false
        });
    }

    private static void LaunchCmdTerminal(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k cd /d \"{path}\"",
            UseShellExecute = false
        });
    }

    private static void LaunchPowerShell(string path)
    {
        try
        {
            // Prefer PowerShell 7 (pwsh), which supports -WorkingDirectory
            Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoExit -WorkingDirectory \"{path}\"",
                UseShellExecute = false
            });
            return;
        }
        catch
        {
            // Fall back to Windows PowerShell 5.1
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -Command \"Set-Location -LiteralPath '{path}'\"",
            UseShellExecute = false
        });
    }

    private static void LaunchGitBash(string path)
    {
        var candidates = new[]
        {
            @"%ProgramFiles%\Git\git-bash.exe",
            @"%ProgramFiles(x86)%\Git\git-bash.exe"
        };

        foreach (var candidate in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = expanded,
                    Arguments = $"--cd=\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }
        }

        throw new InvalidOperationException("Git Bash not found");
    }

    /// <summary>Friendly display name for a Terminal token (empty = auto).</summary>
    private static string TerminalDisplayName(string? terminal)
    {
        return terminal switch
        {
            "WindowsTerminal" => "Windows Terminal",
            "Cmd" => "cmd",
            "PowerShell" => "PowerShell",
            "GitBash" => "Git Bash",
            _ => "terminal"
        };
    }

    /// <summary>
    /// True when the focused element is a text input (editor, find/replace box,
    /// editable folder combo), so number hotkeys should NOT fire while typing.
    /// </summary>
    private bool IsTypingInTextBox()
    {
        return Keyboard.FocusedElement is TextBoxBase;
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
            ? $"Open in VSCode + {TerminalDisplayName(Project.Terminal)} (press {Number}, or {HotkeyLabel}+{Number} anywhere)"
            : $"Run: {Project.Command} (press {Number}, or {HotkeyLabel}+{Number} anywhere)";
    }

    #endregion
}
