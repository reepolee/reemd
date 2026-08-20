using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Reemd.Services;

/// <summary>
/// Integrates with GitHub via the `gh` CLI tool for authentication and background sync.
/// Git operations (commit/push) are run directly via the `git` executable.
/// </summary>
public sealed class GitHubService
{
    private const string GhExe = "gh";
    private const string GitExe = "git";
    private const string ReeRepoOrg = "reepolee";

    public bool IsAuthenticated { get; private set; }
    public string? CurrentUser { get; private set; }
    public string? CurrentRepo { get; private set; }

    private readonly List<string> _usedRepos = new();
    private static readonly string UsedReposPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reemd", "used_repos.json");

    /// <summary>
    /// Repos (nameWithOwner) an issue has been created for, most recent first.
    /// Persisted to used_repos.json so the dialog can populate instantly without a GitHub call.
    /// </summary>
    public IReadOnlyList<string> UsedRepos => _usedRepos;

    /// <summary>
    /// Loads the used-repos list from disk (best-effort, no-op if missing/invalid).
    /// </summary>
    public void LoadUsedRepos()
    {
        try
        {
            if (!File.Exists(UsedReposPath)) return;
            var json = File.ReadAllText(UsedReposPath);
            var repos = JsonSerializer.Deserialize<List<string>>(json);
            if (repos == null) return;
            _usedRepos.Clear();
            _usedRepos.AddRange(repos);
        }
        catch
        {
            // Best-effort — missing/corrupt file just means an empty list.
        }
    }

    /// <summary>
    /// Records a repo as just-used (issue created), moving it to the front of the
    /// used-repos list and persisting to used_repos.json.
    /// </summary>
    public void RecordRepoUsed(string repoNameWithOwner)
    {
        _usedRepos.RemoveAll(r => string.Equals(r, repoNameWithOwner, StringComparison.OrdinalIgnoreCase));
        _usedRepos.Insert(0, repoNameWithOwner);

        try
        {
            var dir = Path.GetDirectoryName(UsedReposPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(UsedReposPath, JsonSerializer.Serialize(_usedRepos));
        }
        catch
        {
            // Best-effort — persistence failure should not block issue creation.
        }
    }

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
    /// Pulls latest changes from the remote (git pull).
    /// </summary>
    public async Task<(bool Success, string Message)> PullAsync(string markdownFolder)
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunGitCommandAsync($"-C \"{markdownFolder}\" pull", 60);
            if (exitCode == 0)
                return (true, stdout.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)
                    ? "Already up to date."
                    : "Pulled latest changes.");
            return (false, $"Git pull failed: {stderr}");
        }
        catch (Exception ex)
        {
            return (false, $"Pull error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the .gitignore rule matching the given repo-relative path (e.g.
    /// "comet.win.reemd.projects.json"), or null when the path is not ignored or the
    /// folder is not a git repo. Used to warn when a per-device config file would not
    /// be committed by the auto-sync (which runs <c>git add -A</c>).
    /// </summary>
    public async Task<string?> GetIgnoreRuleAsync(string markdownFolder, string relativePath)
    {
        var (exitCode, output, _) = await RunGitCommandAsync(
            $"-C \"{markdownFolder}\" check-ignore -v -- \"{relativePath}\"", 15);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            return null;

        // Output looks like:  .gitignore:1:.*\treemd.projects.json
        var rule = output.Trim();
        var tab = rule.IndexOf('\t');
        return tab > 0 ? rule[..tab] : rule;
    }

    /// <summary>
    /// Lists ALL repositories under the reepolee org whose name starts with "ree", fetched
    /// fresh from GitHub every call (not cached, not persisted) — used only by the dialog's
    /// "Reload" action to find repos never sent an issue to before.
    /// </summary>
    public async Task<List<string>> ListReeRepositoriesAsync()
    {
        var (exitCode, output, error) = await RunGhCommandAsync(
            $"repo list {ReeRepoOrg} --json nameWithOwner --limit 200", 30);
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to list repositories: {error}");

        var repos = new List<string>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var nameWithOwner = entry.GetProperty("nameWithOwner").GetString();
            if (nameWithOwner == null) continue;
            var repoName = nameWithOwner.Contains('/') ? nameWithOwner[(nameWithOwner.IndexOf('/') + 1)..] : nameWithOwner;
            if (repoName.StartsWith("ree", StringComparison.OrdinalIgnoreCase))
                repos.Add(nameWithOwner);
        }

        repos.Sort(StringComparer.OrdinalIgnoreCase);
        return repos;
    }

    /// <summary>
    /// Creates a new GitHub issue on the given repo (format "owner/name").
    /// The body is piped via stdin to avoid shell-escaping a multi-line body.
    /// </summary>
    public async Task<(bool Success, string Message)> CreateIssueAsync(string repoNameWithOwner, string title, string body, IEnumerable<string>? labels = null)
    {
        try
        {
            var arguments = $"issue create --repo \"{repoNameWithOwner}\" --title \"{title.Replace("\"", "\\\"")}\" --body-file -";
            if (labels != null)
            {
                foreach (var label in labels)
                    arguments += $" --label \"{label.Replace("\"", "\\\"")}\"";
            }

            var (exitCode, output, error) = await RunProcessAsync(GhExe, arguments, 30, body);
            if (exitCode != 0)
                return (false, $"Failed to create issue: {error}");

            return (true, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"Create issue error: {ex.Message}");
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
    /// Runs an arbitrary process on a background thread and returns the exit code, stdout,
    /// and stderr. When stdinInput is provided, it is written to the process's standard input
    /// and closed, so the child process (e.g. `gh ... --body-file -`) can read it.
    /// </summary>
    private static Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, int timeoutSeconds, string? stdinInput = null)
    {
        return Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinInput != null,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            if (stdinInput != null)
            {
                await process.StandardInput.WriteAsync(stdinInput);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty, $"Command timed out after {timeoutSeconds}s");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (process.ExitCode, stdout ?? string.Empty, stderr ?? string.Empty);
        });
    }
}
