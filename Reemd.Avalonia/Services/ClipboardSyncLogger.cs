using System.IO;

namespace Reemd.Services;

/// <summary>
/// Appends timestamped, payload-free diagnostics for LAN clipboard synchronization.
/// </summary>
public sealed class ClipboardSyncLogger
{
    private readonly string _logPath;

    public ClipboardSyncLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directoryPath = Path.Combine(appData, "Reemd");
        Directory.CreateDirectory(directoryPath);
        _logPath = Path.Combine(directoryPath, "clipboard-sync.log");
    }

    public string LogPath => _logPath;

    public void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
