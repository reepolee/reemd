using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Reemd.Services;

/// <summary>
/// Cross-platform process launching: opening files/folders, VSCode, terminals, and
/// running custom shell commands. Windows and macOS behave differently, so every
/// action routes through here rather than shelling out with OS-specific strings inline.
/// </summary>
public static class ProcessLauncher
{
    /// <summary>Opens a folder in the system file manager (Explorer / Finder).</summary>
    public static void OpenInFileManager(string folderPath)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open", $"\"{folderPath}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{folderPath}\"") { UseShellExecute = true });
    }

    /// <summary>Opens a file with the OS default application for its type.</summary>
    public static void OpenWithDefaultApp(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// Opens the folder in VSCode. Tries the `code` CLI first (resolved via the shell),
    /// then falls back to common install locations for each OS.
    /// </summary>
    public static void LaunchVSCode(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            LaunchVSCodeMac(path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = true });
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
                Process.Start(new ProcessStartInfo(expanded, $"\"{path}\"") { UseShellExecute = true });
                return;
            }
        }

        throw new InvalidOperationException("VSCode not found ('code' not on PATH)");
    }

    /// <summary>
    /// macOS variant of <see cref="LaunchVSCode"/>. The `code` CLI lives in the user's
    /// shell PATH (Homebrew, /usr/local/bin), which a GUI app launched from Finder does
    /// not inherit. UseShellExecute=true also silently no-ops for a bare command that
    /// isn't on PATH (it falls back to `open`) instead of throwing, so the try/catch
    /// fallback above can't be relied on here. Launch the bundled `code` script directly,
    /// then fall back to `code` resolved through the user's login shell.
    /// </summary>
    private static void LaunchVSCodeMac(string path)
    {
        const string bundledCode = "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
        if (File.Exists(bundledCode))
        {
            var psi = new ProcessStartInfo(bundledCode) { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
            return;
        }

        var shell = new ProcessStartInfo("/bin/zsh") { UseShellExecute = false };
        shell.ArgumentList.Add("-lc");
        shell.ArgumentList.Add($"code \"{path}\"");
        Process.Start(shell);
    }

    /// <summary>
    /// Opens a terminal in the folder according to the project's Terminal setting.
    /// Empty/unknown = Auto (Windows Terminal on Windows, Terminal.app on macOS).
    /// </summary>
    public static void LaunchTerminal(string path, string? terminal)
    {
        if (OperatingSystem.IsMacOS())
        {
            LaunchMacTerminal(path, terminal);
            return;
        }

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

    /// <summary>Runs a custom shell command with the project folder as working directory.</summary>
    public static void RunCustomCommand(string command, string path, string name)
    {
        command = command
            .Replace("\"{path}\"", $"\"{path}\"", StringComparison.OrdinalIgnoreCase)
            .Replace("{path}", $"\"{path}\"", StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsMacOS())
        {
            // Run through the user's login shell so the command resolves against their
            // real PATH (Homebrew, /usr/local/bin), which a GUI app launched from Finder
            // does not inherit. Pass the command as a single argument to avoid ambiguity
            // in shell-quoting.
            var psi = new ProcessStartInfo("/bin/zsh")
            {
                WorkingDirectory = path,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
            Process.Start(psi);
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = path,
                UseShellExecute = false
            });
        }
    }

    /// <summary>Friendly display name for a Terminal token (empty = auto).</summary>
    public static string TerminalDisplayName(string? terminal)
    {
        return terminal switch
        {
            "WindowsTerminal" => "Windows Terminal",
            "Cmd" => "cmd",
            "PowerShell" => "PowerShell",
            "GitBash" => "Git Bash",
            "MacTerminal" => "Terminal",
            "ITerm" => "iTerm",
            _ => OperatingSystem.IsMacOS() ? "Terminal" : "terminal"
        };
    }

    private static void LaunchMacTerminal(string path, string? terminal)
    {
        var app = terminal switch
        {
            "ITerm" => "iTerm",
            _ => "Terminal"
        };

        // `open -a <app> <folder>` opens the app focused on that folder. Use the full
        // path and pass each argument individually so paths with spaces survive.
        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(app);
        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }

    private static void LaunchWindowsTerminal(string path)
    {
        Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{path}\"") { UseShellExecute = false });
    }

    private static void LaunchCmdTerminal(string path)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k cd /d \"{path}\"") { UseShellExecute = false });
    }

    private static void LaunchPowerShell(string path)
    {
        try
        {
            // Prefer PowerShell 7 (pwsh), which supports -WorkingDirectory
            Process.Start(new ProcessStartInfo("pwsh", $"-NoExit -WorkingDirectory \"{path}\"") { UseShellExecute = false });
            return;
        }
        catch
        {
            // Fall back to Windows PowerShell 5.1
        }

        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command \"Set-Location -LiteralPath '{path}'\"") { UseShellExecute = false });
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
                Process.Start(new ProcessStartInfo(expanded, $"--cd=\"{path}\"") { UseShellExecute = true });
                return;
            }
        }

        throw new InvalidOperationException("Git Bash not found");
    }
}
