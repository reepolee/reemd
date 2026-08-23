using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Reemd.Dialogs;
using Reemd.Services;

namespace Reemd;

/// <summary>Partial class containing application update checks and installation.</summary>
public partial class MainWindow
{
    private async void BtnUpdateApp_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdateApp.IsEnabled = false;
        try
        {
            SetStatus("Checking for a ReeMD update...");
            var release = await AutoUpdateService.GetLatestReleaseAsync();
            if (release == null)
            {
                SetStatus("ReeMD is up to date.");
                return;
            }

            var dialog = new UpdateDialog(release.Version, _isDarkMode);
            var installUpdate = await dialog.ShowDialog<bool>(this);
            if (!installUpdate)
            {
                SetStatus("Update canceled.");
                return;
            }

            SetStatus($"Downloading ReeMD {release.Version}...");
            var stagedUpdate = await AutoUpdateService.DownloadAndStageAsync(release);
            AutoUpdateService.StartInstaller(stagedUpdate);
            SetStatus("Restarting to install the update...");
            SaveAndClose();

            var applicationLifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            applicationLifetime?.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus($"Update failed: {ex.Message}");
        }
        finally
        {
            if (!_isClosing)
                BtnUpdateApp.IsEnabled = true;
        }
    }
}
