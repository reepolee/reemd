using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Reemd.Models;

namespace Reemd;

public partial class MainWindow
{
    #region Settings Persistence

    private void LoadSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            if (!File.Exists(settingsPath)) return;

            var lines = File.ReadAllLines(settingsPath);
            foreach (var line in lines)
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;

                switch (parts[0].Trim())
                {
                    case "Folder":
                        var folder = parts[1].Trim();
                        if (Directory.Exists(folder))
                            _markdownFolder = folder;
                        break;
                    case "LastFile":
                        _pendingLastFile = parts[1].Trim();
                        break;
                    case "CursorPosition":
                        var cursorData = parts[1].Trim().Split('|');
                        if (cursorData.Length == 4 && int.TryParse(cursorData[1], out var offset)
                            && int.TryParse(cursorData[2], out var selStart)
                            && int.TryParse(cursorData[3], out var selLength))
                        {
                            _loadedCursorPositions[cursorData[0]] = new CursorPosition(offset, selStart, selLength);
                        }
                        break;
                    case "WindowLeft":
                        if (double.TryParse(parts[1].Trim(), out var l)) _savedLeft = l;
                        break;
                    case "WindowTop":
                        if (double.TryParse(parts[1].Trim(), out var t)) _savedTop = t;
                        break;
                    case "WindowWidth":
                        if (double.TryParse(parts[1].Trim(), out var w)) _savedWidth = w;
                        break;
                    case "WindowHeight":
                        if (double.TryParse(parts[1].Trim(), out var h)) _savedHeight = h;
                        break;
                    case "WindowMaximized":
                        if (bool.TryParse(parts[1].Trim(), out var m)) _savedMaximized = m;
                        break;
                    case "FileListColumnWidth":
                        TryRestoreColumnWidth(FileListColumn, parts[1].Trim());
                        break;
                    case "EditorColumnWidth":
                        TryRestoreColumnWidth(EditorColumn, parts[1].Trim());
                        break;
                    case "PreviewColumnWidth":
                        TryRestoreColumnWidth(PreviewColumn, parts[1].Trim());
                        break;
                    case "ScrollRatio":
                        var scrollData = parts[1].Trim().Split('|');
                        if (scrollData.Length == 2 && double.TryParse(scrollData[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ratio))
                            _scrollRatios[scrollData[0]] = Math.Clamp(ratio, 0.0, 1.0);
                        break;
                    case "EditorFontSize":
                        if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var edFontSize))
                            _editorFontSize = Math.Clamp(edFontSize, 8.0, 48.0);
                        break;
                    case "PreviewFontSize":
                        if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var prevFontSize))
                            _previewFontSize = Math.Clamp(prevFontSize, 8.0, 48.0);
                        break;
                    case "DarkMode":
                        if (bool.TryParse(parts[1].Trim(), out var dark))
                            _isDarkMode = dark;
                        break;
                    case "WordWrapEnabled":
                        if (bool.TryParse(parts[1].Trim(), out var wrap))
                            _wordWrapEnabled = wrap;
                        break;
                }
            }

            RestoreWindowPosition();
        }
        catch
        {
        }
    }

    private void SaveWindowPosition()
    {
        _savedLeft = Left;
        _savedTop = Top;
        _savedWidth = Width;
        _savedHeight = Height;
        _savedMaximized = WindowState == WindowState.Maximized;
    }

    private string GetSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "Reemd");
        return Path.Combine(dir, "settings.txt");
    }

    private void SaveSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            var dir = Path.GetDirectoryName(settingsPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var lines = new List<string>
            {
                $"Folder={_markdownFolder}",
                $"LastFile={_currentFilePath ?? ""}",
                $"WindowLeft={_savedLeft}",
                $"WindowTop={_savedTop}",
                $"WindowWidth={_savedWidth}",
                $"WindowHeight={_savedHeight}",
                $"WindowMaximized={_savedMaximized}",
                $"FileListColumnWidth={FileListColumn.Width}",
                $"EditorColumnWidth={EditorColumn.Width}",
                $"PreviewColumnWidth={PreviewColumn.Width}",
                $"EditorFontSize={_editorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"PreviewFontSize={_previewFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"DarkMode={_isDarkMode}",
                $"WordWrapEnabled={_wordWrapEnabled}",
            };

            // Save scroll ratios
            foreach (var kvp in _scrollRatios)
            {
                lines.Add($"ScrollRatio={kvp.Key}|{kvp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            // Save cursor positions
            foreach (var kvp in _cursorPositions)
            {
                var pos = kvp.Value;
                lines.Add($"CursorPosition={kvp.Key}|{pos.CaretIndex}|{pos.SelectionStart}|{pos.SelectionLength}");
            }

            File.WriteAllLines(settingsPath, lines);
        }
        catch
        {
            // Best-effort — settings save should never crash the app
        }
    }

    private void RestoreWindowPosition()
    {
        if (!double.IsNaN(_savedLeft))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _savedLeft;
            Top = _savedTop;
            Width = _savedWidth;
            Height = _savedHeight;

            if (_savedMaximized)
                WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Tries to parse a saved column width value and assign it to the given ColumnDefinition.
    /// Supports star sizes (e.g. "2*"), "Auto", and explicit pixel values (e.g. "250" or "250px").
    /// </summary>
    private static void TryRestoreColumnWidth(System.Windows.Controls.ColumnDefinition column, string value)
    {
        try
        {
            var trimmed = value.Trim();

            if (trimmed.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                column.Width = System.Windows.GridLength.Auto;
                return;
            }

            if (trimmed.EndsWith("*") && trimmed.Length > 1)
            {
                var starValue = trimmed[..^1];
                if (double.TryParse(starValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var starSize))
                {
                    column.Width = new System.Windows.GridLength(starSize, System.Windows.GridUnitType.Star);
                    return;
                }
            }

            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^2].Trim();

            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixelSize))
            {
                column.Width = new System.Windows.GridLength(pixelSize, System.Windows.GridUnitType.Pixel);
            }
        }
        catch
        {
            // Best-effort
        }
    }

    #endregion
}
