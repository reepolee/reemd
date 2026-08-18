using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Reemd.Models;
using Reemd.Services;

namespace Reemd;

/// <summary>
/// Partial class containing folder management: loading, file watching, file list
/// refresh, pinned files, and folder selection UI.
/// </summary>
public partial class MainWindow
{
    #region Folder Management

    private void LoadMarkdownFolder(string folderPath)
    {
        try
        {
            _markdownFolder = string.IsNullOrWhiteSpace(folderPath) ? "." : folderPath;

            if (!Path.IsPathRooted(_markdownFolder))
            {
                _markdownFolder = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, _markdownFolder));
            }

            if (!Directory.Exists(_markdownFolder))
            {
                SetStatus($"Folder not found: {_markdownFolder}");
                return;
            }

            FolderCombo.Text = _markdownFolder;

            _fileList.Clear();
            _cursorPositions.Clear();
            _scrollRatios.Clear();
            _fileContentCache.Clear();
            _currentFilePath = null;
            _isDirty = false;

            Editor.Text = string.Empty;
            UpdatePreview(string.Empty);
            UpdateSavedIndicator(true);

            // Load pinned filenames from .pinned file (hidden system file)
            LoadPinnedFilenames();

            var files = Directory.GetFiles(_markdownFolder, Config.MarkdownFilter)
                .OrderByDescending(f => _pinnedFilenames.Contains(Path.GetFileName(f)) ? 1 : 0)
                .ThenByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                _fileList.Add(new FileEntry
                {
                    Name = fileName,
                    IsPinned = _pinnedFilenames.Contains(fileName)
                });
                _cursorPositions[file] = new CursorPosition(0, 0, 0);
            }

            UpdateFileCount();
            SetStatus($"Loaded {_fileList.Count} file(s) from {_markdownFolder}");

            SetupFileWatcher();

            if (_fileList.Count > 0)
            {
                FileListBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading folder: {ex.Message}");
        }
    }

    private void SetupFileWatcher()
    {
        _fileWatcher?.Dispose();

        try
        {
            _fileWatcher = new FileSystemWatcher(_markdownFolder, Config.MarkdownFilter)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _fileWatcher.Created += FileSystemWatcher_FileCreatedOrDeleted;
            _fileWatcher.Deleted += FileSystemWatcher_FileCreatedOrDeleted;
            _fileWatcher.Changed += FileWatcher_FileChanged;
            _fileWatcher.Renamed += FileSystemWatcher_Renamed;
        }
        catch
        {
        }
    }

    private void FileSystemWatcher_FileCreatedOrDeleted(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshFileList);
    }

    /// <summary>
    /// Fired when a markdown file is modified externally. Refreshes the file list and
    /// reloads the current file if it was changed externally and we have no unsaved changes.
    /// </summary>
    private void FileWatcher_FileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshFileList();

            if (_currentFilePath != null &&
                !_isDirty &&
                string.Equals(e.FullPath, _currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                // Content matches cache = our own save; skip reload to preserve Undo history.
                if (_fileContentCache.TryGetValue(e.FullPath, out var cached) &&
                    cached == File.ReadAllText(e.FullPath))
                    return;

                SetStatus($"Reloaded: {Path.GetFileName(_currentFilePath)} (externally modified)");
                LoadFile(_currentFilePath);
            }
        });
    }

    private void FileSystemWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshFileList);
    }

    private void RefreshFileList()
    {
        try
        {
            var currentSelection = (FileListBox.SelectedItem as FileEntry)?.Name;

            var orderedFiles = Directory.GetFiles(_markdownFolder, Config.MarkdownFilter)
                .OrderByDescending(f => _pinnedFilenames.Contains(Path.GetFileName(f)) ? 1 : 0)
                .ThenByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            _fileList.Clear();
            foreach (var file in orderedFiles)
            {
                var fileName = Path.GetFileName(file);
                _fileList.Add(new FileEntry
                {
                    Name = fileName,
                    IsPinned = _pinnedFilenames.Contains(fileName)
                });
                if (!_cursorPositions.ContainsKey(file))
                    _cursorPositions[file] = new CursorPosition(0, 0, 0);
            }

            UpdateFileCount();

            if (currentSelection != null)
            {
                var match = _fileList.FirstOrDefault(f => f.Name == currentSelection);
                if (match != null)
                    FileListBox.SelectedItem = match;
            }
        }
        catch
        {
        }
    }

    private void UpdateFileCount()
    {
        FileCountText.Text = _fileList.Count.ToString();
        FileCountStatus.Text = $"{_fileList.Count} file(s)";
    }

    private void LoadPinnedFilenames()
    {
        _pinnedFilenames.Clear();
        try
        {
            var pinnedPath = Path.Combine(_markdownFolder, ".pinned");
            if (!File.Exists(pinnedPath)) return;

            var lines = File.ReadAllLines(pinnedPath);
            foreach (var line in lines)
            {
                var name = line.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    _pinnedFilenames.Add(name);
            }
        }
        catch
        {
        }
    }

    private void SavePinnedFilenames()
    {
        try
        {
            var pinnedPath = Path.Combine(_markdownFolder, ".pinned");
            File.WriteAllLines(pinnedPath, _pinnedFilenames);
        }
        catch
        {
        }
    }

    #endregion

    #region Folder Selection

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder with Markdown files",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                FolderCombo.Text = path;
                LoadMarkdownFolder(path);
            }
        }
    }

    private void FolderCombo_LostFocus(object? sender, RoutedEventArgs e)
    {
        var text = FolderCombo.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text) && text != _markdownFolder)
        {
            LoadMarkdownFolder(text);
        }
    }

    private void FolderCombo_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = FolderCombo.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                LoadMarkdownFolder(text);
            }
        }
    }

    /// <summary>
    /// Opens the current markdown folder in the system file manager.
    /// </summary>
    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            ProcessLauncher.OpenInFileManager(_markdownFolder);
        }
        catch
        {
        }
    }

    #endregion
}
