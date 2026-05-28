using System.Diagnostics;
using System.IO;

namespace Reemd.Services;

/// <summary>
/// Integrates with GitHub via the `gh` CLI tool for authentication and background sync.
/// Git operations (commit/push) are run directly via the `git` executable.
/// </summary>
public sealed class GitHubService
{
    private const string GhExe = "gh";
    private const string GitExe = "git";

    public bool IsAuthenticated { get; private set; }
    public string? CurrentUser { get; private set; }
    public string? CurrentRepo { get; private set; }

    /// <summary>
    /// Checks if the `gh` CLI is installed and authenticated.
    /// </summary>
    public async Task<bool> CheckAuthAsync()
    {
        try
        {
            var (exitCode, output, _) = await RunGhCommandAsync("auth status");
            IsAuthenticated = exitCode == 0 && output.Contains("Logged in to", StringComparison.OrdinalIgnoreCase);

            if (IsAuthenticated)
            {
                // Extract the username from e.g. "Logged in to github.com account <user> (keyring)"
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    if (line.Contains("Logged in to", StringComparison.OrdinalIgnoreCase))
                    {
                        // Find the word after "account " or "as "
                        var markers = new[] { "account ", "as " };
                        foreach (var marker in markers)
                        {
                            var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                var after = line[(idx + marker.Length)..].Trim();
                                var spaceIdx = after.IndexOf(' ');
                                CurrentUser = spaceIdx > 0 ? after[..spaceIdx] : after;
                                CurrentUser = CurrentUser?.TrimEnd('.', ')', '(', '\r');
                                break;
                            }
                        }
                        break;
                    }
                }

                // Try to detect current repo
                var (_, repoOutput, _) = await RunGhCommandAsync("repo view --json nameWithOwner -q .nameWithOwner");
                if (!string.IsNullOrWhiteSpace(repoOutput))
                    CurrentRepo = repoOutput.Trim();
            }

            return IsAuthenticated;
        }
        catch
        {
            IsAuthenticated = false;
            CurrentUser = null;
            CurrentRepo = null;
            return false;
        }
    }

    /// <summary>
    /// Commits and pushes changes to GitHub in the background.
    /// Runs git commands directly (not through gh CLI).
    /// </summary>
    public async Task<(bool Success, string Message)> CommitAndPushAsync(string filePath, string markdownFolder)
    {
        try
        {
            // First check auth
            if (!IsAuthenticated)
            {
                var authResult = await CheckAuthAsync();
                if (!authResult)
                    return (false, "Not authenticated with GitHub CLI. Run 'gh auth login' first.");
            }

            // Check if a remote is configured
            var (remoteExit, remoteOut, _) = await RunGitCommandAsync($"-C \"{markdownFolder}\" remote -v", 15);
            if (remoteExit != 0 || string.IsNullOrWhiteSpace(remoteOut))
                return (false, "No git remote configured. Set one with: git remote add origin <url>");

            // Build a commit message with the filename
            var fileName = Path.GetFileName(filePath);
            var commitMessage = $"Update {fileName} [Reemd auto-save]";

            // Run git add
            var (addExit, _, addErr) = await RunGitCommandAsync($"-C \"{markdownFolder}\" add -A", 30);
            if (addExit != 0)
                return (false, $"Git add failed: {addErr}");

            // Check if there's anything to commit
            var (statusExit, statusOut, _) = await RunGitCommandAsync($"-C \"{markdownFolder}\" status --porcelain", 30);
            if (statusExit != 0 || string.IsNullOrWhiteSpace(statusOut))
                return (true, "No changes to push.");

            // Run git commit
            var (commitExit, _, commitErr) = await RunGitCommandAsync($"-C \"{markdownFolder}\" commit -m \"{commitMessage.Replace("\"", "\\\"")}\"", 30);
            if (commitExit != 0)
                return (false, $"Git commit failed: {commitErr}");

            // Run git push
            var (pushExit, pushOut, pushErr) = await RunGitCommandAsync($"-C \"{markdownFolder}\" push", 60);
            if (pushExit != 0)
                return (false, $"Git push failed: {pushErr}");

            return (true, "Changes committed and pushed to GitHub.");
        }
        catch (Exception ex)
        {
            return (false, $"GitHub sync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a `gh` command asynchronously.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGhCommandAsync(string arguments, int timeoutSeconds = 15)
    {
        return await RunProcessAsync(GhExe, arguments, timeoutSeconds);
    }

    /// <summary>
    /// Runs a `git` command asynchronously.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCommandAsync(string arguments, int timeoutSeconds = 15)
    {
        return await RunProcessAsync(GitExe, arguments, timeoutSeconds);
    }

    /// <summary>
    /// Runs an arbitrary process asynchronously and returns the exit code, stdout, and stderr.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = process.WaitForExit(timeoutSeconds * 1000);
        if (!completed)
        {
            process.Kill();
            return (-1, string.Empty, $"Command timed out after {timeoutSeconds}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout ?? string.Empty, stderr ?? string.Empty);
    }
}
