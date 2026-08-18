namespace Reemd.Models;

/// <summary>
/// A project shown as a toolbar button. Clicking (or pressing its number hotkey)
/// opens the folder in VSCode and starts a terminal in it — or runs a custom
/// command when <see cref="Command"/> is set.
/// </summary>
public class ProjectShortcut
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional custom command run instead of the default VSCode + terminal launch.
    /// Supports {path} (project folder, quoted) and {name} placeholders.
    /// Leave empty for the default behavior.
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Terminal emulator used by the default launch (ignored when <see cref="Command"/>
    /// is set). Empty = auto (Windows Terminal, fallback cmd). Other values:
    /// "WindowsTerminal", "Cmd", "PowerShell", "GitBash".
    /// </summary>
    public string Terminal { get; set; } = "";
}
