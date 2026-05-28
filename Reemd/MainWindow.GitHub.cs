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
