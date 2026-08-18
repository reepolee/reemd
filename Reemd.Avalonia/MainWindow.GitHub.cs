using System.IO;
using Avalonia.Interactivity;
using Reemd.Dialogs;

namespace Reemd;

public partial class MainWindow
{
    private void BtnNewIssue_Click(object? sender, RoutedEventArgs e)
    {
        OpenNewIssueDialog();
    }

    /// <summary>
    /// Opens the New GitHub Issue dialog (Ctrl+Alt+I, in-app or as a global hotkey).
    /// </summary>
    internal async void OpenNewIssueDialog()
    {
        var dialog = new NewIssueDialog(_gitHubService, _isDarkMode);
        var result = await dialog.ShowDialog<bool>(this);
        if (result)
            SetStatus("GitHub issue created.");
    }

    #region GitHub Sync

    /// <summary>True while a GitHub sync (pull + commit + push) is running.</summary>
    private bool _isSyncing;

    /// <summary>
    /// Schedules a GitHub sync 15 seconds after the last save.
    /// </summary>
    private void ScheduleGitHubSync()
    {
        _gitHubSyncTimer.Stop();
        _gitHubSyncTimer.Start();
    }

    private async void GitHubSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentFilePath == null) return;

        // If a sync is already running, re-arm the timer so a follow-up sync runs
        // after the current one finishes — never run concurrent git commands.
        if (_isSyncing)
        {
            _gitHubSyncTimer.Stop();
            _gitHubSyncTimer.Start();
            return;
        }

        _isSyncing = true;
        try
        {
            await RunGitHubSyncAsync();
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task RunGitHubSyncAsync()
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
