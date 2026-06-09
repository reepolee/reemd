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

namespace Reemd;

/// <summary>
/// Main window of the Reemd markdown editor.
/// Manages file browsing, raw markdown editing, live preview, auto-save, and GitHub sync.
/// Broken into partial files by feature area (see MainWindow.*.cs).
/// </summary>
public partial class MainWindow : Window
{


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
        Interval = TimeSpan.FromMilliseconds(Config.AutoSaveIntervalMs)
    };

    private readonly DispatcherTimer _previewTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(Config.PreviewDebounceMs)
    };

    private readonly DispatcherTimer _gitHubSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(Config.GitHubSyncDebounceMs)
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

        // Set window icon from embedded resource (works with PublishSingleFile)
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Reemd.icon.ico");
            if (stream != null)
            {
                using var icon = new System.Drawing.Icon(stream);
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
        }
        catch
        {
            // Window icon is best-effort
        }
        FileListBox.ItemsSource = _fileList;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _previewTimer.Tick += PreviewTimer_Tick;
        _gitHubSyncTimer.Tick += GitHubSyncTimer_Tick;

        // Wire events BEFORE loading anything — ensures NavigationCompleted doesn't get missed
        Preview.NavigationCompleted += Preview_NavigationCompleted;
        Preview.CoreWebView2InitializationCompleted += Preview_CoreWebView2InitializationCompleted;

        // Load settings first — restores window position, column widths, saved font sizes, etc.
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

        // Single folder load — runs after all settings are applied
        LoadMarkdownFolder(folder);

        // Restore cursor positions from settings before selecting any file
        foreach (var path in _loadedCursorPositions.Keys)
        {
            _cursorPositions[path] = _loadedCursorPositions[path];
        }
        _loadedCursorPositions.Clear();

        // Re-select the last file from settings
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

        // Force preview font sync once after the window first loads
        RoutedEventHandler onLoaded = null!;
        onLoaded = (_, _) =>
        {
            this.Loaded -= onLoaded;
            ApplyPreviewFontSize();
        };
        this.Loaded += onLoaded;

        // Window-level keyboard/mouse handlers — fire before tunneling to focused control
        this.PreviewKeyDown += MainWindow_PreviewKeyDown;
        this.PreviewMouseWheel += Window_PreviewMouseWheel;

        // Sync on focus — refreshes 'Last sync' time when returning to the app
        this.Activated += (_, _) => ScheduleGitHubSync();

        // Intercept paste at the command level — catches Ctrl+V, right-click paste, Shift+Insert
        Editor.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (_, e) => HandlePaste(e)));

        _ = CheckGitHubAuthAsync();
    }

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

    #region Lifecycle

    /// <summary>
    /// Called from the tray Exit command to save and actually close.
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

        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
            return;

        SaveWindowPosition();
        SaveSettings();

        // Hide to tray instead of closing
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Synchronous save path used during shutdown.
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
}
