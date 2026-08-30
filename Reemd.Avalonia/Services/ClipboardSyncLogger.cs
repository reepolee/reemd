using System.IO;

namespace Reemd.Services;

/// <summary>
/// Writes timestamped, payload-free LAN clipboard diagnostics, resetting the file per app run.
/// </summary>
public sealed class ClipboardSyncLogger
{
    private readonly string _logPath;
    private readonly object _write_lock = new();
    private long _entry_number;

    public ClipboardSyncLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directoryPath = Path.Combine(appData, "Reemd");
        Directory.CreateDirectory(directoryPath);
        _logPath = Path.Combine(directoryPath, "clipboard-sync.log");

        try
        {
            File.WriteAllText(_logPath, string.Empty);
        }
        catch
        {
        }

        Log($"Clipboard log started: platform={ClipboardBundle.GetCurrentPlatform()}, process={Environment.ProcessId}");
    }

    public string LogPath => _logPath;

    public void Log(string message)
    {
        try
        {
            lock (_write_lock)
            {
                _entry_number++;
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{_entry_number:D5}] {message}";
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
