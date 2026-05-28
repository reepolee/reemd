using System.IO;

namespace Reemd.Services;

/// <summary>
/// Appends timestamped log entries to a file for debugging GitHub sync operations.
/// Log file is stored at %LOCALAPPDATA%/Reemd/sync.log.
/// </summary>
public sealed class SyncLogger
{
    private readonly string _logPath;

    public SyncLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "Reemd");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "sync.log");
    }

    /// <summary>Full path to the log file.</summary>
    public string LogPath => _logPath;

    /// <summary>Writes a single log line with a timestamp.</summary>
    public void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // Swallow — logging should never crash the app
        }
    }

    /// <summary>Writes a header section to visually separate sync cycles.</summary>
    public void BeginSync()
    {
        Log("--- Begin GitHub sync ---");
    }

    /// <summary>Writes a footer after a sync cycle completes.</summary>
    public void EndSync(bool success, string message)
    {
        Log($"--- Sync {(success ? "OK" : "FAILED")}: {message}");
        Log(string.Empty);
    }
}
