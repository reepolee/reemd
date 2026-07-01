using System.IO;
using System.Threading.Tasks;

namespace Reemd;

public partial class MainWindow
{
    #region GitHub Sync

    /// <summary>
    /// Schedules a GitHub sync 15 seconds after the last save.
    /// Every call resets the timer (debounce pattern),
    /// so rapid edits only trigger one sync after the user stops.
    /// </summary>
    private void ScheduleGitHubSync()
    {
        _gitHubSyncTimer.Stop();
        _gitHubSyncTimer.Start();
    }

    private async void GitHubSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentFilePath == null) return;

        GitHubStatusText.Text = "\u2601\ufe0f Syncing...";

        // Only reload from disk if we're not dirty (no unsaved edits)
        if (!_isDirty)
        {
            var (pullOk, pullMsg) = await _gitHubService.PullAsync(_markdownFolder);
            if (pullOk && File.Exists(_currentFilePath))
            {
                var diskContent = await File.ReadAllTextAsync(_currentFilePath);
                if (diskContent != Editor.Text)
                {
                    _isLoadingDocument = true;
                    Editor.Text = diskContent;
                    _fileContentCache[_currentFilePath] = diskContent;
                    _isDirty = false;
                    UpdateSavedIndicator(true);
                    _isLoadingDocument = false;
                    _previewTimer.Stop();
                    _previewTimer.Start();
                }
            }
        }

        // Then commit and push any local changes
        var (success, message) = await _gitHubService.CommitAndPushAsync(_currentFilePath, _markdownFolder);

        if (success)
        {
            _lastSyncTime = DateTime.Now;
            LastSyncText.Text = $"Last sync: {_lastSyncTime.Value.ToShortTimeString()}";

            if (message == "No changes to push.")
            {
                GitHubStatusText.Text = "\u2601\ufe0f Up to date";
            }
            else
            {
                GitHubStatusText.Text = "\u2601\ufe0f Synced";
                SetStatus("Synced to GitHub");
            }
        }
        else
        {
            GitHubStatusText.Text = $"\u2601\ufe0f {message}";
            SetStatus($"Sync failed: {message}");
        }
    }

    #endregion

    /// <summary>
    /// Manually triggers a git pull and reloads the current file if it changed on disk.
    /// </summary>
    internal async Task ForcePullAsync()
    {
        if (_currentFilePath == null) return;

        GitHubStatusText.Text = "☁️ Pulling...";
        SetStatus("Pulling from remote...");

        var (success, message) = await _gitHubService.PullAsync(_markdownFolder);

        if (success)
        {
            _lastSyncTime = DateTime.Now;
            LastSyncText.Text = $"Last sync: {_lastSyncTime.Value.ToShortTimeString()}";
            GitHubStatusText.Text = "☁️ Pulled";
            SetStatus(message);

            // Reload the current file if it was updated by the pull
            if (File.Exists(_currentFilePath))
            {
                var diskContent = await File.ReadAllTextAsync(_currentFilePath);
                if (diskContent != Editor.Text)
                {
                    _isLoadingDocument = true;
                    Editor.Text = diskContent;
                    _fileContentCache[_currentFilePath] = diskContent;
                    _isDirty = false;
                    UpdateSavedIndicator(true);
                    _isLoadingDocument = false;
                    _previewTimer.Stop();
                    _previewTimer.Start();
                }
            }
        }
        else
        {
            GitHubStatusText.Text = $"☁️ {message}";
            SetStatus($"Pull failed: {message}");
        }
    }

    #region GitHub Auth

    private async Task CheckGitHubAuthAsync()
    {
        try
        {
            var isAuth = await _gitHubService.CheckAuthAsync();
            if (isAuth)
            {
                var user = _gitHubService.CurrentUser ?? "unknown";
                GitHubStatusText.Text = $"\u2601\ufe0f GitHub: {user}";
                ScheduleGitHubSync();
            }
            else
            {
                GitHubStatusText.Text = "\u2601\ufe0f Not authenticated";
            }
        }
        catch
        {
            GitHubStatusText.Text = "\u2601\ufe0f gh CLI not found";
        }
    }

    #endregion
}
