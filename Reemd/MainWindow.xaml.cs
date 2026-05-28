using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Reemd.Models;
using Reemd.Services;
using Microsoft.Win32;

namespace Reemd;

/// <summary>
/// Main window of the Reemd markdown editor.
/// Manages file browsing, raw markdown editing, live preview, auto-save, and GitHub sync.
/// </summary>
public partial class MainWindow : Window
{
    private const int AutoSaveIntervalMs = 5000;
    private const int PreviewDebounceMs = 400;
    private const int GitHubSyncDebounceMs = 15000;
    private const string MarkdownFilter = "*.md";

    private readonly MarkdownConverter _markdownConverter = new();
    private readonly GitHubService _gitHubService = new();

    private readonly HashSet<string> _pinnedFilenames = [];
    private readonly ObservableCollection<FileEntry> _fileList = [];
    private readonly Dictionary<string, CursorPosition> _cursorPositions = [];
    private readonly Dictionary<string, string> _fileContentCache = [];
    private string _markdownFolder = ".";
    private string? _currentFilePath;
    private bool _isLoadingDocument;
    private bool _isDirty;
    private bool _isClosing;
    private bool _forceClose;
    private double _savedLeft = double.NaN;
    private double _savedTop = double.NaN;
    private double _savedWidth = double.NaN;
    private double _savedHeight = double.NaN;
    private bool _savedMaximized;

    private readonly List<int> _findResults = [];
    private int _currentFindIndex = -1;

    private readonly DispatcherTimer _autoSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(AutoSaveIntervalMs)
    };

    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(PreviewDebounceMs)
    };

    private readonly DispatcherTimer _gitHubSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(GitHubSyncDebounceMs)
    };

    private ScrollViewer? _editorScrollViewer;
    private bool _isSyncingScroll;
    private bool _isPreviewReady;
    private readonly Dictionary<string, double> _scrollRatios = [];
    private FileSystemWatcher? _fileWatcher;
    private double _editorFontSize = 13;
    private double _previewFontSize = 14;
    private bool _isDarkMode;
    private bool _wordWrapEnabled;
    private string? _pendingPreviewHtml;
    private DateTime? _lastSyncTime;
    private bool IsEditorFocused => Keyboard.FocusedElement == Editor;
    private string? _pendingLastFile;
    private readonly Dictionary<string, CursorPosition> _loadedCursorPositions = [];

    public MainWindow(string? startupFolder = null)
    {
        InitializeComponent();
        FileListBox.ItemsSource = _fileList;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _previewTimer.Tick += PreviewTimer_Tick;
        _gitHubSyncTimer.Tick += GitHubSyncTimer_Tick;

        // Wire events BEFORE loading anything — ensures NavigationCompleted doesn't get missed
        Preview.NavigationCompleted += Preview_NavigationCompleted;
        Preview.CoreWebView2InitializationCompleted += Preview_CoreWebView2InitializationCompleted;

        // Load settings first — restores window position, column widths, saved font sizes, etc.
        // NOTE: this does NOT call LoadMarkdownFolder; that happens in a single call below
        // so ALL settings (including PreviewFontSize) are applied before the preview renders.
        LoadSettings();

        // Determine folder — startup arg overrides the folder from settings
        string folder;
        if (!string.IsNullOrWhiteSpace(startupFolder))
        {
            folder = startupFolder;
            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(Path.Combine(
                    Environment.CurrentDirectory, folder));
        }
        else
        {
            folder = _markdownFolder;
        }

        // Single folder load — this runs AFTER all settings are applied,
        // so the preview renders with the correct saved font size.
        LoadMarkdownFolder(folder);

        // Restore cursor positions from settings BEFORE selecting any file,
        // so LoadFile → RestoreCursorPosition can find saved positions.
        // (LoadMarkdownFolder clears _cursorPositions, so we re-apply them.)
        foreach (var path in _loadedCursorPositions.Keys)
        {
            _cursorPositions[path] = _loadedCursorPositions[path];
        }
        _loadedCursorPositions.Clear();

        // Re-select the last file from settings (stored by LoadSettings)
        if (_pendingLastFile != null)
        {
            var pendingName = Path.GetFileName(_pendingLastFile);
            var match = _fileList.FirstOrDefault(f => f.Name == pendingName);
            if (match != null)
            {
                FileListBox.SelectedItem = match;
            }
            _pendingLastFile = null;
        }

        // Apply saved font sizes to editor and preview
        ApplyEditorFontSize();
        ApplyPreviewFontSize();

        // Apply saved word wrap state
        Editor.TextWrapping = _wordWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;

        // Apply saved theme to editor
        ApplyTheme();

        // Wire scroll sync after controls are loaded
        Editor.Loaded += OnEditorLoaded;
        Preview.Loaded += OnPreviewLoaded;

        // Force preview font sync once after the window first loads (removes itself after first fire).
        // This ensures the preview is rendered with the correct saved font size after startup
        // when CoreWebView2 is fully initialized.
        RoutedEventHandler onLoaded = null!;
        onLoaded = (_, _) =>
        {
            this.Loaded -= onLoaded;
            ApplyPreviewFontSize();
        };
        this.Loaded += onLoaded;

        // Window-level keyboard/mouse handlers — fire before tunneling to focused control,
        // so they work even when the WebView2 preview has focus.
        this.PreviewKeyDown += MainWindow_PreviewKeyDown;
        this.PreviewMouseWheel += Window_PreviewMouseWheel;

        // Sync on focus — refreshes 'Last sync' time when returning to the app
        this.Activated += (_, _) => ScheduleGitHubSync();

        _ = CheckGitHubAuthAsync();
    }

    #region Folder Management

    private void LoadMarkdownFolder(string folderPath)
    {
        try
        {
            _markdownFolder = string.IsNullOrWhiteSpace(folderPath) ? "." : folderPath;

            if (!Path.IsPathRooted(_markdownFolder))
            {
                _markdownFolder = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, _markdownFolder));
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

            var files = Directory.GetFiles(_markdownFolder, MarkdownFilter)
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
            _fileWatcher = new FileSystemWatcher(_markdownFolder, MarkdownFilter)
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
        Dispatcher.Invoke(() => RefreshFileList());
    }

    /// <summary>
    /// Fired when a markdown file is modified externally (by another app or tool).
    /// Refreshes the file list and reloads the current file if it was changed externally
    /// and we have no unsaved changes. Skips if the content matches our cache,
    /// which means it was our own save (no Undo history loss).
    /// </summary>
    private void FileWatcher_FileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
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
        Dispatcher.Invoke(() => RefreshFileList());
    }

    private void RefreshFileList()
    {
        try
        {
            var currentSelection = (FileListBox.SelectedItem as FileEntry)?.Name;

            // Fully rebuild the file list sorted by last write time (most recent first).
            // This ensures that after saving, the saved file jumps to the top.
            var orderedFiles = Directory.GetFiles(_markdownFolder, MarkdownFilter)
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

    /// <summary>
    /// Loads pinned filenames from the .pinned file in the markdown folder.
    /// Each line is a filename to pin to the top of the list.
    /// </summary>
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
            // Best-effort — if .pinned can't be read, no pins
        }
    }

    /// <summary>
    /// Saves the current pinned filenames to the .pinned file in the markdown folder.
    /// </summary>
    private void SavePinnedFilenames()
    {
        try
        {
            var pinnedPath = Path.Combine(_markdownFolder, ".pinned");
            File.WriteAllLines(pinnedPath, _pinnedFilenames);
        }
        catch
        {
            // Best-effort
        }
    }

    #endregion

    #region File Loading & Saving

    private void LoadFile(string filePath)
    {
        if (_isClosing) return;

        // Save previous file first before switching
        if (_isDirty && _currentFilePath != null)
        {
            _ = SaveCurrentFileAsync();
        }

        if (!File.Exists(filePath))
        {
            SetStatus($"File not found: {Path.GetFileName(filePath)}");
            return;
        }

        // Read content — do NOT touch editor state on failure
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading {Path.GetFileName(filePath)}: {ex.Message}");
            return;
        }

        // Only now update state — everything succeeded
        _isLoadingDocument = true;
        try
        {
            _currentFilePath = filePath;
            _fileContentCache[filePath] = content;

            Editor.Text = content;
            UpdatePreview(content, _previewFontSize);

            UpdateTitle(filePath);
            SetStatus($"Editing: {Path.GetFileName(filePath)}");

            RestoreCursorPosition(filePath);

            _isDirty = false;
            UpdateSavedIndicator(true);

            // Restore per-file scroll position after layout is complete
            Dispatcher.BeginInvoke(RestorePerFileScroll, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            SetStatus($"Error rendering preview: {ex.Message}");
        }
        finally
        {
            _isLoadingDocument = false;
        }
    }

    private async Task SaveCurrentFileAsync()
    {
        if (_currentFilePath == null) return;

        try
        {
            SaveCursorPosition(_currentFilePath);

            var markdown = Editor.Text;

            await File.WriteAllTextAsync(_currentFilePath, markdown);
            _fileContentCache[_currentFilePath] = markdown;

            _isDirty = false;
            UpdateSavedIndicator(true);
            ScheduleGitHubSync();

            // Refresh file list to re-sort by last write time — suppress
            // SelectionChanged so we don't re-load the same file unnecessarily.
            _isLoadingDocument = true;
            RefreshFileList();
            _isLoadingDocument = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Save error: {ex.Message}");
            UpdateSavedIndicator(false);
        }
    }

    private async Task AutoSaveCurrentFileAsync()
    {
        if (_currentFilePath == null || !_isDirty || _isLoadingDocument) return;
        await SaveCurrentFileAsync();
    }

    private void UpdateTitle(string? filePath)
    {
        var fileName = filePath != null ? Path.GetFileName(filePath) : "Untitled";
        Title = $"Reemd - {fileName}";
    }

    #endregion

    #region Preview

    private void UpdatePreview(string markdown, double? previewFontSize = null)
    {
        try
        {
            var size = previewFontSize ?? _previewFontSize;
            var html = _markdownConverter.ConvertToHtml(markdown, size, _isDarkMode, _markdownFolder);
            _isPreviewReady = false;

            if (Preview.CoreWebView2 != null)
            {
                Preview.NavigateToString(html);
                _pendingPreviewHtml = null;
            }
            else
            {
                // CoreWebView2 not yet initialized — store HTML to render once ready
                _pendingPreviewHtml = html;
            }
        }
        catch
        {
            // Preview is best-effort — never crash the editor on render failures
        }
    }

    private void ApplyEditorFontSize()
    {
        Editor.FontSize = _editorFontSize;
        ShowCombinedFontSizes();
        SaveSettings();
    }

    private void ApplyPreviewFontSize()
    {
        ShowCombinedFontSizes();
        if (!string.IsNullOrEmpty(Editor.Text))
        {
            UpdatePreview(Editor.Text, _previewFontSize);
        }
    }

    private void ShowCombinedFontSizes()
    {
        FontSizeText.Text = $"Editor: {_editorFontSize}px";
        PreviewFontSizeText.Text = $"Preview: {_previewFontSize}px";
    }

    /// <summary>
    /// Fires after a 400ms pause in typing to refresh the preview.
    /// </summary>
    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        UpdatePreview(Editor.Text, _previewFontSize);

        // Re-sync preview scroll to match editor after update
        Dispatcher.BeginInvoke(SyncEditorToPreview, DispatcherPriority.Background);
    }

    #endregion

    #region Cursor Position Memory

    private void SaveCursorPosition(string filePath)
    {
        try
        {
            _cursorPositions[filePath] = new CursorPosition(
                Editor.CaretIndex,
                Editor.SelectionStart,
                Editor.SelectionLength);
        }
        catch
        {
        }
    }

    private void RestoreCursorPosition(string filePath)
    {
        if (!_cursorPositions.TryGetValue(filePath, out var pos)) return;

        try
        {
            var textLen = Editor.Text.Length;
            Editor.CaretIndex = Math.Min(pos.CaretIndex, textLen);
            Editor.SelectionStart = Math.Min(pos.SelectionStart, textLen);
            Editor.SelectionLength = Math.Min(pos.SelectionLength, textLen - Editor.SelectionStart);
        }
        catch
        {
        }
    }

    #endregion

    #region Find Bar

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceBar.Visibility = Visibility.Collapsed;
        FindTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        FindTextBox.Focus();
    }

    private void ShowReplaceBar()
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceBar.Visibility = Visibility.Visible;
        FindTextBox.Text = "";
        ReplaceTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        FindTextBox.Focus();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ReplaceBar.Visibility = Visibility.Collapsed;
        FindTextBox.Text = "";
        ReplaceTextBox.Text = "";
        FindMatchCount.Text = "";
        _findResults.Clear();
        _currentFindIndex = -1;
        Editor.Focus();
    }

    private void DoFind()
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            FindMatchCount.Text = "";
            _findResults.Clear();
            _currentFindIndex = -1;
            Editor.Select(0, 0);
            return;
        }

        _findResults.Clear();
        var text = Editor.Text;
        int index = 0;
        int searchLen = searchText.Length;
        while ((index = text.IndexOf(searchText, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            _findResults.Add(index);
            index += searchLen;
        }

        if (_findResults.Count > 0)
        {
            _currentFindIndex = 0;
            SelectFindMatch(0);
            FindMatchCount.Text = $"1/{_findResults.Count}";
        }
        else
        {
            _currentFindIndex = -1;
            Editor.Select(0, 0);
            FindMatchCount.Text = "No results";
        }
    }

    private void FindNext()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }
        _currentFindIndex = (_currentFindIndex + 1) % _findResults.Count;
        SelectFindMatch(_currentFindIndex);
        FindMatchCount.Text = $"{_currentFindIndex + 1}/{_findResults.Count}";
        Editor.Focus();
    }

    private void FindPrevious()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }
        _currentFindIndex = (_currentFindIndex - 1 + _findResults.Count) % _findResults.Count;
        SelectFindMatch(_currentFindIndex);
        FindMatchCount.Text = $"{_currentFindIndex + 1}/{_findResults.Count}";
        Editor.Focus();
    }

    private void SelectFindMatch(int matchIndex)
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText) || matchIndex < 0 || matchIndex >= _findResults.Count) return;

        var start = _findResults[matchIndex];
        Editor.SelectionStart = start;
        Editor.SelectionLength = searchText.Length;
        Editor.CaretIndex = start + searchText.Length;

        // WPF TextBox auto-scrolls to show the caret when CaretIndex is set
    }

    private void FindTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        DoFind();
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    FindPrevious();
                else
                    FindNext();
                e.Handled = true;
                break;
            case Key.Escape:
                HideFindBar();
                e.Handled = true;
                break;
        }
    }

    private void FindPrevBtn_Click(object sender, RoutedEventArgs e)
    {
        FindPrevious();
    }

    private void FindNextBtn_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
    }

    private void FindCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        HideFindBar();
    }

    private void DoReplace()
    {
        if (_findResults.Count == 0)
        {
            DoFind();
            if (_findResults.Count == 0) return;
        }

        var replaceText = ReplaceTextBox.Text;
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var currentPos = _findResults[_currentFindIndex];
        var text = Editor.Text;

        // Replace the current match
        Editor.Text = text.Remove(currentPos, searchText.Length).Insert(currentPos, replaceText);

        // Re-run find to refresh positions (text has changed)
        DoFind();

        // If there are still results, the new current match position is shifted.
        // Adjust the index to point to the match right after the replaced text.
        // Since we replaced at currentPos and DoFind resets to index 0,
        // find the first match at or after (currentPos + replaceText.Length)
        for (int i = 0; i < _findResults.Count; i++)
        {
            if (_findResults[i] >= currentPos + replaceText.Length)
            {
                _currentFindIndex = i;
                SelectFindMatch(i);
                FindMatchCount.Text = $"{i + 1}/{_findResults.Count}";
                return;
            }
        }

        // No more matches after this position — wrap to first
        if (_findResults.Count > 0)
        {
            _currentFindIndex = 0;
            SelectFindMatch(0);
            FindMatchCount.Text = $"1/{_findResults.Count}";
        }
    }

    private void ReplaceAll()
    {
        var searchText = FindTextBox.Text;
        var replaceText = ReplaceTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        var text = Editor.Text;
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(searchText, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            text = text.Remove(index, searchText.Length).Insert(index, replaceText);
            index += replaceText.Length;
            count++;
        }

        if (count > 0)
        {
            Editor.Text = text;
            Editor.Focus();
            SetStatus($"Replaced {count} occurrence(s)");
        }

        // Refresh find results
        _findResults.Clear();
        _currentFindIndex = -1;
        FindMatchCount.Text = "";
    }

    private void ReplaceTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                DoReplace();
                e.Handled = true;
                break;
            case Key.Escape:
                HideFindBar();
                e.Handled = true;
                break;
        }
    }

    private void ReplaceBtn_Click(object sender, RoutedEventArgs e)
    {
        DoReplace();
    }

    private void ReplaceAllBtn_Click(object sender, RoutedEventArgs e)
    {
        ReplaceAll();
    }

    private void ReplaceCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        HideFindBar();
    }

    #endregion

    private void UpdateCursorPositionText()
    {
        if (_isLoadingDocument) return;

        try
        {
            int caretIndex = Editor.CaretIndex;
            var textBefore = Editor.Text.AsSpan(0, Math.Min(caretIndex, Editor.Text.Length));
            int line = 1;
            int col = 1;
            for (int i = 0; i < textBefore.Length; i++)
            {
                if (textBefore[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }

            CursorPositionText.Text = $"Ln {line}, Col {col}";
        }
        catch
        {
        }
    }

    #region Event Handlers

    private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDocument) return;

        var selectedEntry = FileListBox.SelectedItem as FileEntry;
        if (selectedEntry == null) return;

        _ = AutoSaveCurrentFileAsync();

        var fullPath = Path.Combine(_markdownFolder, selectedEntry.Name);
        LoadFile(fullPath);
    }

    /// <summary>
    /// Toggles the pin state of a file. Pinned files appear at the top of the list.
    /// </summary>
    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var entry = button?.DataContext as FileEntry;
        if (entry == null) return;

        // Toggle pin state
        entry.IsPinned = !entry.IsPinned;

        if (entry.IsPinned)
            _pinnedFilenames.Add(entry.Name);
        else
            _pinnedFilenames.Remove(entry.Name);

        SavePinnedFilenames();

        // Re-sort the list so pinned files appear at the top
        var currentSelection = (FileListBox.SelectedItem as FileEntry)?.Name;

        var sorted = _fileList
            .OrderByDescending(f => f.IsPinned ? 1 : 0)
            .ThenByDescending(f =>
            {
                var fullPath = Path.Combine(_markdownFolder, f.Name);
                try { return File.GetLastWriteTime(fullPath); }
                catch { return DateTime.MinValue; }
            })
            .ToList();

        _fileList.Clear();
        foreach (var item in sorted)
            _fileList.Add(item);

        // Re-select
        if (currentSelection != null)
        {
            var match = _fileList.FirstOrDefault(f => f.Name == currentSelection);
            if (match != null)
                FileListBox.SelectedItem = match;
        }

        SetStatus(entry.IsPinned
            ? $"Pinned: {entry.Name}"
            : $"Unpinned: {entry.Name}");
    }

    private void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var selectedEntry = FileListBox.SelectedItem as FileEntry;
        if (selectedEntry == null) return;

        var fullPath = Path.Combine(_markdownFolder, selectedEntry.Name);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void FileListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F2 — rename selected file
        if (e.Key == Key.F2)
        {
            var selectedEntry = FileListBox.SelectedItem as FileEntry;
            if (selectedEntry == null) return;

            RenameFile(selectedEntry.Name);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Shows a rename dialog for the given filename (without path).
    /// Renames the file on disk and updates all internal state.
    /// </summary>
    private void RenameFile(string fileName)
    {
        var oldPath = Path.Combine(_markdownFolder, fileName);
        if (!File.Exists(oldPath)) return;

        var oldNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        // Show rename dialog
        var bg = _isDarkMode ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Color.FromRgb(0xF0, 0xF0, 0xF0);
        var fg = _isDarkMode ? Colors.White : Colors.Black;

        var dialog = new Window
        {
            Title = "Rename File",
            Width = 400,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg)
        };

        var stack = new StackPanel { Margin = new Thickness(12) };

        stack.Children.Add(new TextBlock
        {
            Text = "New name:",
            Foreground = new SolidColorBrush(fg),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var textBox = new TextBox
        {
            Text = oldNameWithoutExt,
            Padding = new Thickness(6, 3, 6, 3)
        };
        textBox.Focus();
        textBox.SelectAll();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var okBtn = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 70,
            Height = 24,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Width = 70,
            Height = 24
        };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);

        stack.Children.Add(textBox);
        stack.Children.Add(buttonPanel);
        dialog.Content = stack;

        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        okBtn.Click += (_, _) => dialog.DialogResult = true;
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;

        var result = dialog.ShowDialog();
        if (result != true) return;

        var newName = textBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        // Ensure .md extension
        if (!newName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            newName += ".md";

        var newPath = Path.Combine(_markdownFolder, newName);

        // Check if the name actually changed (on case-insensitive filesystems like Windows NTFS,
        // a case-only change like "file.md" → "FILE.md" requires special handling — see below)
        bool onlyCaseChanged = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
        if (onlyCaseChanged && string.Equals(newPath, oldPath, StringComparison.Ordinal))
            return;

        // Check if target already exists
        if (File.Exists(newPath))
        {
            SetStatus($"Cannot rename: '{newName}' already exists");
            return;
        }

        // Validate filename
        var invalidChars = Path.GetInvalidFileNameChars();
        if (newName.Any(c => invalidChars.Contains(c)))
        {
            SetStatus($"Invalid characters in filename");
            return;
        }

        try
        {
            if (!onlyCaseChanged)
            {
                File.Move(oldPath, newPath);
            }
            else
            {
                // Case-only rename on case-insensitive filesystem:
                // Move to a temp name first, then to the desired case.
                var tempPath = oldPath + ".tmp_rename";
                File.Move(oldPath, tempPath);
                File.Move(tempPath, newPath);
            }

            // Update internal state to use the new path
            if (_cursorPositions.TryGetValue(oldPath, out var cursorPos))
            {
                _cursorPositions[newPath] = cursorPos;
                _cursorPositions.Remove(oldPath);
            }

            if (_scrollRatios.TryGetValue(oldPath, out var scrollRatio))
            {
                _scrollRatios[newPath] = scrollRatio;
                _scrollRatios.Remove(oldPath);
            }

            if (_fileContentCache.TryGetValue(oldPath, out var content))
            {
                _fileContentCache[newPath] = content;
                _fileContentCache.Remove(oldPath);
            }

            // If this was the current file, update the current path
            if (string.Equals(_currentFilePath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentFilePath = newPath;
                Editor.Text = content ?? File.ReadAllText(newPath);
                UpdateTitle(newPath);
            }

            // Update last write time so the file jumps to the top of the sorted list
            File.SetLastWriteTime(newPath, DateTime.Now);

            // Refresh the file list and select the renamed file
            RefreshFileList();
            var newFileName = Path.GetFileName(newPath);
            var match = _fileList.FirstOrDefault(f => f.Name == newFileName);
            if (match != null)
            {
                FileListBox.SelectedItem = match;
            }

            SetStatus($"Renamed to {newName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Rename failed: {ex.Message}");
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingDocument) return;

        _isDirty = true;
        UpdateSavedIndicator(false);

        // Reset auto-save timer
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();

        // Reset preview timer (debounce — only renders after 400ms of no typing)
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateCursorPositionText();
    }

    private void BtnScrollTop_Click(object sender, RoutedEventArgs e)
    {
        ScrollEditorToTop();
    }

    private void BtnScrollBottom_Click(object sender, RoutedEventArgs e)
    {
        ScrollEditorToBottom();
    }

    private void ScrollEditorToTop()
    {
        Editor.CaretIndex = 0;
        Editor.ScrollToHome();
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollToTop();
        }
        Dispatcher.BeginInvoke(SyncEditorToPreview, DispatcherPriority.Background);
    }

    private void ScrollEditorToBottom()
    {
        Editor.CaretIndex = Editor.Text.Length;
        Editor.ScrollToEnd();
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollToBottom();
        }
        Dispatcher.BeginInvoke(SyncEditorToPreview, DispatcherPriority.Background);
    }

    /// <summary>
    /// Fires before the event tunnels to the focused/under-mouse control.
    /// Ctrl+Scroll over a panel = that panel's font (position-based).
    /// Ctrl+Shift+Scroll = opposite panel's font.
    /// </summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+Shift+Scroll → force the OPPOSITE panel's font
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (IsEditorFocused)
            {
                _previewFontSize = e.Delta > 0
                    ? Math.Min(_previewFontSize + 1, 48)
                    : Math.Max(_previewFontSize - 1, 8);
                ApplyPreviewFontSize();
                SaveSettings();
            }
            else
            {
                _editorFontSize = e.Delta > 0
                    ? Math.Min(_editorFontSize + 1, 48)
                    : Math.Max(_editorFontSize - 1, 8);
                ApplyEditorFontSize();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Scroll → context-sensitive by mouse position
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var pos = Mouse.GetPosition(Editor);
            bool overEditor = pos.X >= 0 && pos.Y >= 0 && pos.X < Editor.ActualWidth && pos.Y < Editor.ActualHeight;

            if (overEditor)
            {
                // Over editor → change editor font
                _editorFontSize = e.Delta > 0
                    ? Math.Min(_editorFontSize + 1, 48)
                    : Math.Max(_editorFontSize - 1, 8);
                ApplyEditorFontSize();
            }
            else
            {
                // Over preview (or anywhere else) → change preview font
                _previewFontSize = e.Delta > 0
                    ? Math.Min(_previewFontSize + 1, 48)
                    : Math.Max(_previewFontSize - 1, 8);
                ApplyPreviewFontSize();
                SaveSettings();
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Window-level handler for keyboard shortcuts — fires before tunneling reaches
    /// the focused control (TextBox or WebView2). Handles Alt+Z for word wrap and
    /// Ctrl+Shift+Plus/Minus/0 for preview font size, even when WebView2 has focus.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Alt+Z — toggle word wrap
        // When Alt is held, WPF reports e.Key = Key.System and e.SystemKey = actual key.
        if ((Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) &&
            (e.Key == Key.Z || e.SystemKey == Key.Z))
        {
            ToggleWordWrap();
            e.Handled = true;
            return;
        }

        // Ctrl+Plus/Minus/0 (no Shift) — context-sensitive: font of the active panel
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    if (IsEditorFocused)
                    {
                        _editorFontSize = Math.Min(_editorFontSize + 1, 48);
                        ApplyEditorFontSize();
                    }
                    else
                    {
                        _previewFontSize = Math.Min(_previewFontSize + 1, 48);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    if (IsEditorFocused)
                    {
                        _editorFontSize = Math.Max(_editorFontSize - 1, 8);
                        ApplyEditorFontSize();
                    }
                    else
                    {
                        _previewFontSize = Math.Max(_previewFontSize - 1, 8);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    if (IsEditorFocused)
                    {
                        _editorFontSize = 13;
                        ApplyEditorFontSize();
                    }
                    else
                    {
                        _previewFontSize = 14;
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Shift+Plus/Minus/0 — forces the OPPOSITE panel's font
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    if (IsEditorFocused)
                    {
                        _previewFontSize = Math.Min(_previewFontSize + 1, 48);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    else
                    {
                        _editorFontSize = Math.Min(_editorFontSize + 1, 48);
                        ApplyEditorFontSize();
                    }
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    if (IsEditorFocused)
                    {
                        _previewFontSize = Math.Max(_previewFontSize - 1, 8);
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    else
                    {
                        _editorFontSize = Math.Max(_editorFontSize - 1, 8);
                        ApplyEditorFontSize();
                    }
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    if (IsEditorFocused)
                    {
                        _previewFontSize = 14;
                        ApplyPreviewFontSize();
                        SaveSettings();
                    }
                    else
                    {
                        _editorFontSize = 13;
                        ApplyEditorFontSize();
                    }
                    e.Handled = true;
                    return;
            }
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+Z — toggle word wrap
        // When Alt is held, WPF reports e.Key = Key.System and e.SystemKey = actual key.
        // Also check e.SystemKey since Alt triggers WM_SYSKEYDOWN.
        if ((Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) &&
            (e.Key == Key.Z || e.SystemKey == Key.Z))
        {
            ToggleWordWrap();
            e.Handled = true;
            return;
        }

        // Ctrl+Tab / Ctrl+Shift+Tab — file navigation (needs non-strict modifier check)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Tab)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                SelectPreviousFile();
            else
                SelectNextFile();
            e.Handled = true;
            Editor.Focus();
            return;
        }

        // Ctrl+Shift+C — insert code block (```)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            e.Key == Key.C)
        {
            InsertCodeBlock();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+I — inline code (`)
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            e.Key == Key.I)
        {
            InsertMarkdownWrapper("`");
            e.Handled = true;
            return;
        }

        // F3 / Shift+F3 — find next/previous (no Ctrl needed)
        if (e.Key == Key.F3)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
            return;
        }

        // Ctrl+Plus/Minus/0 is handled at Window level (MainWindow_PreviewKeyDown)
        // for context-sensitive behavior. Only other Ctrl-based shortcuts remain here.

        // Markdown formatting and editor shortcuts (exact Ctrl only, no other modifiers)
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.S:
                _ = SaveCurrentFileAsync();
                e.Handled = true;
                break;
            case Key.N:
                CreateNewFile();
                e.Handled = true;
                break;
            case Key.B:
                InsertMarkdownWrapper("**");
                e.Handled = true;
                break;
            case Key.I:
                InsertMarkdownWrapper("*");
                e.Handled = true;
                break;
            case Key.K:
                InsertLinkMarkdown();
                e.Handled = true;
                break;
            case Key.Home:
                ScrollEditorToTop();
                e.Handled = true;
                break;
            case Key.End:
                ScrollEditorToBottom();
                e.Handled = true;
                break;
            case Key.F:
                ShowFindBar();
                e.Handled = true;
                break;
            case Key.H:
                ShowReplaceBar();
                e.Handled = true;
                break;
            case Key.G:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    FindPrevious();
                else
                    FindNext();
                e.Handled = true;
                break;
        }
    }

    private void CreateNewFile()
    {
        if (string.IsNullOrWhiteSpace(_markdownFolder)) return;
        try
        {
            // Find a unique filename
            int counter = 1;
            string fileName;
            do
            {
                fileName = $"Untitled-{counter}.md";
                counter++;
            } while (File.Exists(Path.Combine(_markdownFolder, fileName)));

            var fullPath = Path.Combine(_markdownFolder, fileName);
            File.WriteAllText(fullPath, "");

            RefreshFileList();
            var createdMatch = _fileList.FirstOrDefault(f => f.Name == fileName);
            if (createdMatch != null)
                FileListBox.SelectedItem = createdMatch;
            Editor.Focus();
            SetStatus($"Created {fileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Error creating file: {ex.Message}");
        }
    }

    private void SelectNextFile()
    {
        if (_fileList.Count == 0) return;
        if (FileListBox.SelectedIndex < _fileList.Count - 1)
            FileListBox.SelectedIndex++;
        else
            FileListBox.SelectedIndex = 0; // wrap to first
    }

    private void SelectPreviousFile()
    {
        if (_fileList.Count == 0) return;
        if (FileListBox.SelectedIndex > 0)
            FileListBox.SelectedIndex--;
        else
            FileListBox.SelectedIndex = _fileList.Count - 1; // wrap to last
    }

    #region Context Menu Handlers

    private void ContextMenu_Bold_Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdownWrapper("**");
    }

    private void ContextMenu_Italic_Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdownWrapper("*");
    }

    private void ContextMenu_InlineCode_Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdownWrapper("`");
    }

    private void ContextMenu_CodeBlock_Click(object sender, RoutedEventArgs e)
    {
        InsertCodeBlock();
    }

    private void ContextMenu_Link_Click(object sender, RoutedEventArgs e)
    {
        InsertLinkMarkdown();
    }

    #endregion

    /// <summary>
    /// Wraps the current selection with the given delimiter (e.g. ** for bold, * for italic).
    /// </summary>
    private void InsertMarkdownWrapper(string delimiter)
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionLength;

        if (selLen > 0)
        {
            var selected = Editor.Text.Substring(selStart, selLen);
            Editor.Text = Editor.Text.Remove(selStart, selLen)
                .Insert(selStart, $"{delimiter}{selected}{delimiter}");
            Editor.SelectionStart = selStart;
            Editor.SelectionLength = selLen + delimiter.Length * 2;
        }
        else
        {
            // No selection, insert placeholder
            var placeholder = $"{delimiter}text{delimiter}";
            Editor.Text = Editor.Text.Insert(selStart, placeholder);
            Editor.SelectionStart = selStart + delimiter.Length;
            Editor.SelectionLength = 4; // select "text"
        }

        Editor.Focus();
    }

    /// <summary>
    /// Wraps the selection in a markdown code block (```).
    /// With selection: wraps selected text in ```\n...\n```.
    /// Without selection: inserts a placeholder code block and selects "code".
    /// </summary>
    private void InsertCodeBlock()
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionLength;

        if (selLen > 0)
        {
            var selected = Editor.Text.Substring(selStart, selLen);
            var replacement = $"```\n{selected}\n```";
            Editor.Text = Editor.Text.Remove(selStart, selLen).Insert(selStart, replacement);
            Editor.SelectionStart = selStart;
            Editor.SelectionLength = replacement.Length;
        }
        else
        {
            var replacement = "```\ncode\n```";
            Editor.Text = Editor.Text.Insert(selStart, replacement);
            Editor.SelectionStart = selStart + 4;
            Editor.SelectionLength = 4;
        }

        Editor.Focus();
    }

    /// <summary>
    /// Inserts a markdown link at the cursor position.
    /// </summary>
    private void InsertLinkMarkdown()
    {
        var selStart = Editor.SelectionStart;
        var selLen = Editor.SelectionLength;

        if (selLen > 0)
        {
            var selected = Editor.Text.Substring(selStart, selLen);
            var link = $"[{selected}](url)";
            Editor.Text = Editor.Text.Remove(selStart, selLen).Insert(selStart, link);
            Editor.SelectionStart = selStart + selLen + 3;
            Editor.SelectionLength = 3; // select "url"
        }
        else
        {
            var link = "[link text](url)";
            Editor.Text = Editor.Text.Insert(selStart, link);
            Editor.SelectionStart = selStart + 1;
            Editor.SelectionLength = 9; // select "link text"
        }

        Editor.Focus();
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        _ = AutoSaveCurrentFileAsync();
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _markdownFolder,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        ApplyTheme();
        SetStatus(_isDarkMode ? "Dark theme" : "Light theme");
    }

    /// <summary>
    /// Creates an ItemContainerStyle for the file list that styles the pin button
    /// to match the current theme — just sets cursor to hand.
    /// Foreground is NOT set so the 📌 emoji renders in its natural color.
    /// </summary>
    private static Style CreatePinButtonStyle()
    {
        var style = new Style(typeof(ListBoxItem));

        var btnStyle = new Style(typeof(Button));
        btnStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        style.Resources.Add(typeof(Button), btnStyle);

        return style;
    }

    private void ApplyTheme()
    {
        if (_isDarkMode)
        {
            // Dark theme
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            // Sidebar
            SidebarBorder.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            SidebarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FileListHeader.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            FileCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            FileListBox.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            FileListBox.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));                            FileListBox.ItemContainerStyle = CreatePinButtonStyle();
            FileCountStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            SidebarFooter.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

            // Editor
            EditorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            Editor.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            Editor.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));

            // Preview
            PreviewBorder.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            PreviewBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            Preview.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E);

            // Grid splitters
            SidebarSplitter.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            EditorPreviewSplitter.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));

            // Find bar
            FindBar.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            FindBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FindTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FindTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            FindTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            FindMatchCount.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

            // Replace bar
            ReplaceBar.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            ReplaceBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            ReplaceTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            ReplaceTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            ReplaceTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));            // Status bar
            AppStatusBar.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            AppStatusBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            StatusText.Foreground = new SolidColorBrush(Colors.White);
            FontSizeText.Foreground = new SolidColorBrush(Colors.White);
            PreviewFontSizeText.Foreground = new SolidColorBrush(Colors.White);
            CursorPositionText.Foreground = new SolidColorBrush(Colors.White);
            GitHubStatusText.Foreground = new SolidColorBrush(Colors.White);
            LastSyncText.Foreground = new SolidColorBrush(Colors.White);

            // Toolbar
            FolderCombo.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            FolderCombo.Foreground = new SolidColorBrush(Colors.White);

            BtnToggleTheme.Content = "☀️";
        }
        else
        {
            // Light theme
            Background = SystemColors.WindowBrush;

            // Sidebar
            SidebarBorder.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            SidebarBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            FileListHeader.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            FileCountText.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            FileListBox.Background = SystemColors.WindowBrush;
            FileListBox.Foreground = SystemColors.WindowTextBrush;                            FileListBox.ItemContainerStyle = CreatePinButtonStyle();
            FileCountStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            SidebarFooter.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));

            // Editor
            EditorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Editor.Background = SystemColors.WindowBrush;
            Editor.Foreground = SystemColors.WindowTextBrush;

            // Preview
            PreviewBorder.Background = new SolidColorBrush(Colors.White);
            PreviewBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            Preview.DefaultBackgroundColor = System.Drawing.Color.White;

            // Grid splitters
            SidebarSplitter.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
            EditorPreviewSplitter.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

            // Find bar
            FindBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            FindBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            FindTextBox.Background = new SolidColorBrush(Colors.White);
            FindTextBox.Foreground = SystemColors.WindowTextBrush;
            FindTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
            FindMatchCount.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            // Replace bar
            ReplaceBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            ReplaceBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            ReplaceTextBox.Background = new SolidColorBrush(Colors.White);
            ReplaceTextBox.Foreground = SystemColors.WindowTextBrush;
            ReplaceTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));

            // Status bar
            AppStatusBar.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            AppStatusBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            StatusText.Foreground = SystemColors.WindowTextBrush;

            FontSizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            PreviewFontSizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            CursorPositionText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            GitHubStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            LastSyncText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            // Toolbar
            FolderCombo.Background = SystemColors.WindowBrush;
            FolderCombo.Foreground = SystemColors.WindowTextBrush;

            BtnToggleTheme.Content = "🌙";
        }

        // Re-render preview with the new theme colors
        if (!string.IsNullOrEmpty(Editor.Text))
        {
            UpdatePreview(Editor.Text, _previewFontSize);
        }
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

    /// <summary>
    /// Called from the tray Exit command to save and actually close.
    /// Synchronous to ensure ExitApp() doesn't race to Shutdown() before the save completes.
    /// </summary>
    internal void SaveAndClose()
    {
        _forceClose = true;
        _isClosing = true;
        _autoSaveTimer.Stop();
        _previewTimer.Stop();
        _gitHubSyncTimer.Stop();

        SaveWindowPosition();

        SaveCurrentFileSync();
        SaveSettings();

        // Now close without hiding to tray
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If SaveAndClose() already handled everything, let the close proceed
        if (_forceClose)
            return;

        // Save window position/size before hiding to tray
        SaveWindowPosition();
        SaveSettings();

        // Hide to tray instead of closing
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Synchronous save path used during shutdown.
    /// Simply writes Editor.Text directly to disk.
    /// </summary>
    private void SaveCurrentFileSync()
    {
        if (_currentFilePath == null) return;

        try
        {
            SaveCursorPosition(_currentFilePath);

            File.WriteAllText(_currentFilePath, Editor.Text);
            _fileContentCache[_currentFilePath] = Editor.Text;

            _isDirty = false;
            UpdateSavedIndicator(true);
        }
        catch (Exception ex)
        {
            SetStatus($"Save error: {ex.Message}");
            UpdateSavedIndicator(false);
        }
    }

    #endregion

    #region Scroll Sync

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        _editorScrollViewer = FindVisualChild<ScrollViewer>(Editor);
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollChanged += OnEditorScrollChanged;
            RestoreEditorScroll();
        }
    }

    private void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        // Ensure CoreWebView2 is initialized — this is required before NavigateToString will work.
        // Without this call, CoreWebView2InitializationCompleted may never fire.
        _ = Preview.EnsureCoreWebView2Async();
    }

    private void Preview_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            // Disable WebView2's built-in browser zoom (Ctrl+Scroll/Plus/Minus) so it
            // doesn't intercept our Ctrl+Shift+Scroll/Plus/Minus preview font size control.
            Preview.CoreWebView2.Settings.IsZoomControlEnabled = false;

            Preview.CoreWebView2.WebMessageReceived += OnPreviewWebMessageReceived;

            // If there's pending HTML from before initialization, render it now
            if (_pendingPreviewHtml != null)
            {
                _isPreviewReady = false;
                Preview.NavigateToString(_pendingPreviewHtml);
                _pendingPreviewHtml = null;
            }

            // Re-apply preview font size now that WebView2 is ready,
            // ensuring the correct saved font size is always displayed.
            ApplyPreviewFontSize();
        }
    }

    private void Preview_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isPreviewReady = true;

        // Inject scroll sync script into the page
        _ = Preview.ExecuteScriptAsync(
            "(function(){ window.__reemdScrollRatio=0; window.addEventListener('scroll',function(){var sh=document.documentElement.scrollHeight-document.documentElement.clientHeight;var r=sh>0?document.documentElement.scrollTop/sh:0;window.__reemdScrollRatio=r;try{window.chrome.webview.postMessage(JSON.stringify({type:'scroll',ratio:r}))}catch(e){}}); })();");

        // Restore scroll position for this file after navigation
        if (_currentFilePath != null && _scrollRatios.TryGetValue(_currentFilePath, out var ratio) && ratio > 0)
        {
            _ = Preview.ExecuteScriptAsync(
                "document.documentElement.scrollTop=" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*(document.documentElement.scrollHeight-document.documentElement.clientHeight)");
        }
    }

    private void OnEditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll) return;

        if (_currentFilePath != null)
        {
            _scrollRatios[_currentFilePath] = _editorScrollViewer!.ScrollableHeight > 0
                ? _editorScrollViewer.VerticalOffset / _editorScrollViewer.ScrollableHeight
                : 0;
        }

        if (_isPreviewReady)
        {
            _ = SyncEditorScrollToPreviewAsync();
        }
    }

    private async Task SyncEditorScrollToPreviewAsync()
    {
        if (_editorScrollViewer == null) return;

        var ratio = _editorScrollViewer.ScrollableHeight > 0
            ? _editorScrollViewer.VerticalOffset / _editorScrollViewer.ScrollableHeight
            : 0;

        try
        {
            await Preview.ExecuteScriptAsync(
                "document.documentElement.scrollTop=" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*(document.documentElement.scrollHeight-document.documentElement.clientHeight)");
        }
        catch
        {
        }
    }

    private void OnPreviewWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_isSyncingScroll || _editorScrollViewer == null || _currentFilePath == null) return;

        try
        {
            var msg = JsonSerializer.Deserialize<ScrollMessage>(e.TryGetWebMessageAsString());
            if (msg?.type != "scroll") return;

            _isSyncingScroll = true;
            var ratio = Math.Clamp(msg.ratio, 0.0, 1.0);
            _editorScrollViewer.ScrollToVerticalOffset(_editorScrollViewer.ScrollableHeight * ratio);
            _scrollRatios[_currentFilePath] = ratio;
        }
        catch
        {
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private class ScrollMessage
    {
        public string type { get; set; } = "";
        public double ratio { get; set; }
    }

    private void RestoreEditorScroll()
    {
        if (_editorScrollViewer == null || _currentFilePath == null) return;
        if (!_scrollRatios.TryGetValue(_currentFilePath, out var ratio) || ratio <= 0) return;
        if (_editorScrollViewer.ScrollableHeight <= 0) return;

        _isSyncingScroll = true;
        try
        {
            _editorScrollViewer.ScrollToVerticalOffset(
                _editorScrollViewer.ScrollableHeight * ratio);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void RestorePerFileScroll()
    {
        RestoreEditorScroll();
        _ = SyncEditorScrollToPreviewAsync();
    }

    private void SyncEditorToPreview()
    {
        if (_isPreviewReady)
        {
            _ = SyncEditorScrollToPreviewAsync();
        }
    }

    /// <summary>
    /// Recursively searches the visual tree for a child of type T.
    /// </summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
                return t;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }

        return null;
    }

    #endregion

    #region Folder Selection

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder with Markdown files",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            FolderCombo.Text = dialog.FolderName;
            LoadMarkdownFolder(dialog.FolderName);
        }
    }

    private void FolderCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = FolderCombo.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text) && text != _markdownFolder)
        {
            LoadMarkdownFolder(text);
        }
    }

    private void FolderCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = FolderCombo.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                LoadMarkdownFolder(text);
            }
            Keyboard.ClearFocus();
        }
    }

    #endregion

    #region Status Updates

    /// <summary>
    /// Toggles word wrap on the editor and logs the new state to the status bar.
    /// </summary>
    private void ToggleWordWrap()
    {
        _wordWrapEnabled = !_wordWrapEnabled;
        Editor.TextWrapping = _wordWrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SetStatus(_wordWrapEnabled ? "Word wrap: ON" : "Word wrap: OFF");
        SaveSettings();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void UpdateSavedIndicator(bool saved)
    {
        SavedIndicator.Text = saved ? "\U0001f4be Saved" : "\U0001f4be Modified";
        SavedIndicator.Foreground = saved
            ? new SolidColorBrush(_isDarkMode
                ? Color.FromRgb(0x81, 0xC7, 0x84)  // lighter green for dark mode
                : Color.FromRgb(0x2E, 0x7D, 0x32))  // original green for light mode
            : new SolidColorBrush(_isDarkMode
                ? Color.FromRgb(0xEF, 0x9A, 0x9A)  // lighter red for dark mode
                : Color.FromRgb(0xC6, 0x28, 0x28));  // original red for light mode
    }

    #endregion

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

    private void RestoreWindowPosition()
    {
        if (double.IsNaN(_savedLeft) || double.IsNaN(_savedTop))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _savedLeft;
        Top = _savedTop;
        Width = _savedWidth;
        Height = _savedHeight;
        WindowState = _savedMaximized ? WindowState.Maximized : WindowState.Normal;
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
                $"EditorFontSize={_editorFontSize}",
                $"PreviewFontSize={_previewFontSize}",
                $"DarkMode={_isDarkMode}",
                $"WordWrapEnabled={_wordWrapEnabled}"
            };

            foreach (var kvp in _scrollRatios)
            {
                lines.Add($"ScrollRatio={kvp.Key}|{kvp.Value:F6}");
            }

            foreach (var kvp in _cursorPositions)
            {
                lines.Add($"CursorPosition={kvp.Key}|{kvp.Value.CaretIndex}|{kvp.Value.SelectionStart}|{kvp.Value.SelectionLength}");
            }

            File.WriteAllLines(settingsPath, lines);
        }
        catch
        {
        }
    }

    private static string GetSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Reemd", "settings.txt");
    }

    /// <summary>
    /// Tries to parse a saved column width string and apply it to the given ColumnDefinition.
    /// Accepts formats like "250" (pixels), "*" (star), or "2*" (weighted star).
    /// Uses manual parsing to avoid edge cases with GridLengthConverter.
    /// </summary>
    private static void TryRestoreColumnWidth(ColumnDefinition column, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        try
        {
            value = value.Trim();

            if (value.EndsWith("*"))
            {
                // Star value: "*", "1.5*", "2*"
                var starPart = value[..^1];
                var starValue = string.IsNullOrWhiteSpace(starPart) ? 1.0 : double.Parse(starPart, System.Globalization.CultureInfo.InvariantCulture);
                column.Width = new GridLength(starValue, GridUnitType.Star);
            }
            else if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                column.Width = GridLength.Auto;
            }
            else
            {
                // Pixel value: "250", "451"
                var pixelValue = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                column.Width = new GridLength(pixelValue, GridUnitType.Pixel);
            }
        }
        catch
        {
            // Best-effort — if the saved width is invalid, keep the default
        }
    }

    #endregion
}
