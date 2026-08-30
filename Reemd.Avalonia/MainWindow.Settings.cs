using System.IO;
using Avalonia.Controls;
using Reemd.Models;
using Reemd.Services;

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
                        TryRestoreColumnWidth(MainContentGrid.ColumnDefinitions[0], parts[1].Trim());
                        break;
                    case "EditorColumnWidth":
                        TryRestoreColumnWidth(EditorPreviewGrid.ColumnDefinitions[0], parts[1].Trim());
                        break;
                    case "PreviewColumnWidth":
                        TryRestoreColumnWidth(EditorPreviewGrid.ColumnDefinitions[2], parts[1].Trim());
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
                    case "ProjectHotkeyModifiers":
                        _projectHotkeyToken = parts[1].Trim();
                        break;
                    case "ClipboardChannel":
                        var clipboardChannel = parts[1].Trim();
                        if (ClipboardSyncService.IsValidChannel(clipboardChannel))
                            _clipboardChannel = clipboardChannel;
                        break;
                    case "ClipboardPeers":
                        var clipboardPeers = parts[1].Trim();
                        if (TryParseClipboardPeers(clipboardPeers, out _))
                            _clipboardPeers = clipboardPeers;
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
        _savedLeft = Position.X;
        _savedTop = Position.Y;
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
                $"FileListColumnWidth={MainContentGrid.ColumnDefinitions[0].Width}",
                $"EditorColumnWidth={EditorPreviewGrid.ColumnDefinitions[0].Width}",
                $"PreviewColumnWidth={EditorPreviewGrid.ColumnDefinitions[2].Width}",
                $"EditorFontSize={_editorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"PreviewFontSize={_previewFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                $"DarkMode={_isDarkMode}",
                $"WordWrapEnabled={_wordWrapEnabled}",
                $"ProjectHotkeyModifiers={_projectHotkeyToken}",
                $"ClipboardChannel={_clipboardChannel}",
                $"ClipboardPeers={_clipboardPeers}",
            };

            foreach (var kvp in _scrollRatios)
            {
                lines.Add($"ScrollRatio={kvp.Key}|{kvp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

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
            Position = new Avalonia.PixelPoint((int)_savedLeft, (int)_savedTop);
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
    private static void TryRestoreColumnWidth(ColumnDefinition column, string value)
    {
        try
        {
            var trimmed = value.Trim();

            if (trimmed.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                column.Width = GridLength.Auto;
                return;
            }

            if (trimmed.EndsWith("*") && trimmed.Length > 1)
            {
                var starValue = trimmed[..^1];
                if (double.TryParse(starValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var starSize))
                {
                    column.Width = new GridLength(starSize, GridUnitType.Star);
                    return;
                }
            }

            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^2].Trim();

            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixelSize))
            {
                column.Width = new GridLength(pixelSize, GridUnitType.Pixel);
            }
        }
        catch
        {
            // Best-effort
        }
    }

    #endregion

    private void ClipboardChannelBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateClipboardChannel();
    }

    private void ClipboardChannelBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;

        UpdateClipboardChannel();
        Editor.Focus();
    }

    private void ClipboardPeersBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateClipboardPeers();
    }

    private void ClipboardPeersBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;

        UpdateClipboardPeers();
        Editor.Focus();
    }

    private void UpdateClipboardChannel()
    {
        var clipboardChannel = ClipboardChannelBox.Text?.Trim() ?? string.Empty;
        if (!ClipboardSyncService.IsValidChannel(clipboardChannel))
        {
            ClipboardChannelBox.Text = _clipboardChannel;
            SetStatus("Clipboard channel uses letters, numbers, dots, dashes, and underscores only");
            return;
        }

        if (clipboardChannel == _clipboardChannel) return;

        _clipboardChannel = clipboardChannel;
        _clipboardSyncService.UpdateChannel(clipboardChannel);
        SaveSettings();
        SetStatus($"Clipboard sync channel: {clipboardChannel}");
    }

    private void UpdateClipboardPeers()
    {
        var clipboardPeers = ClipboardPeersBox.Text?.Trim() ?? string.Empty;
        if (!TryParseClipboardPeers(clipboardPeers, out var peerAddresses))
        {
            ClipboardPeersBox.Text = _clipboardPeers;
            SetStatus("LAN peers must be comma-separated IPv4 addresses");
            return;
        }

        var normalizedPeers = string.Join(", ", peerAddresses);
        if (normalizedPeers == _clipboardPeers) return;

        _clipboardPeers = normalizedPeers;
        ClipboardPeersBox.Text = normalizedPeers;
        _clipboardSyncService.UpdatePeers(peerAddresses);
        SaveSettings();
        SetStatus($"Clipboard TCP peers: {peerAddresses.Length}");
    }

    private static bool TryParseClipboardPeers(string peers, out string[] peerAddresses)
    {
        var peerTokens = peers.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (peerTokens.Any(peer => !ClipboardSyncService.IsValidPeerAddress(peer)))
        {
            peerAddresses = [];
            return false;
        }

        peerAddresses = peerTokens.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    private void BtnOpenClipboardLog_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            ProcessLauncher.OpenWithDefaultApp(_clipboardSyncService.LogPath);
        }
        catch (Exception exception)
        {
            SetStatus($"Cannot open clipboard log: {exception.Message}");
        }
    }
}
