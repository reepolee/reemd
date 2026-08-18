using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Reemd.Services;

namespace Reemd;

/// <summary>
/// Application entry point. Manages the system tray icon and global hotkey.
/// </summary>
public partial class App : System.Windows.Application
{
    private const uint VK_SPACE = 0x20;
    private const uint VK_I = 0x49;
    private const uint VK_1 = 0x31; // VK_0 = 0x30 .. VK_9 = 0x39

    private TaskbarIcon? _trayIcon;
    private HotKeyService? _hotKeyService;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Use first command-line argument as startup folder, if provided
        string? startupFolder = e.Args.Length > 0 ? e.Args[0] : null;

        _mainWindow = new MainWindow(startupFolder);

        SetupTrayIcon();

        _hotKeyService = new HotKeyService(_mainWindow);
        _hotKeyService.HotKeyPressed += OnHotKeyPressed;

        // Re-register hotkeys whenever the project list changes, so the global
        // Ctrl+Shift+N keys always match the current project count.
        _mainWindow.ProjectShortcutsChanged += RegisterHotKeys;

        RegisterHotKeys();

        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Reemd - Markdown Editor",
            Visibility = Visibility.Visible
        };

        _trayIcon.Icon = LoadAppIcon() ?? CreateDefaultIcon();

        // Create context menu
        var showItem = new System.Windows.Controls.MenuItem
        {
            Header = "Show",
            Command = new RelayCommand(_ => ShowWindow())
        };

        var exitItem = new System.Windows.Controls.MenuItem
        {
            Header = "Exit",
            Command = new RelayCommand(_ => ExitApp())
        };

        _trayIcon.ContextMenu = new System.Windows.Controls.ContextMenu();
        _trayIcon.ContextMenu.Items.Add(showItem);
        _trayIcon.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        _trayIcon.ContextMenu.Items.Add(exitItem);

        // Double-click to show
        _trayIcon.DoubleClickCommand = new RelayCommand(_ => ShowWindow());
    }

    private void OnHotKeyPressed(string name)
    {
        if (_mainWindow == null) return;

        switch (name)
        {
            case "ToggleWindow":
                if (_mainWindow.IsVisible)
                    _mainWindow.Hide();
                else
                    ShowWindow();
                break;
            case "NewIssue":
                _mainWindow.OpenNewIssueDialog();
                break;
            default:
                // Ctrl+Shift+1..9 — launch the matching project shortcut
                if (name.StartsWith("Project", StringComparison.Ordinal) &&
                    int.TryParse(name.AsSpan("Project".Length), out var number))
                {
                    _mainWindow.LaunchProjectByIndex(number - 1);
                }
                break;
        }
    }

    /// <summary>
    /// (Re)registers all global hotkeys. Project hotkeys track the current project
    /// count so Ctrl+Shift+N is only claimed for projects that actually exist.
    /// </summary>
    private void RegisterHotKeys()
    {
        if (_hotKeyService == null || _mainWindow == null) return;

        _hotKeyService.Reset();

        _hotKeyService.AddHotKey("ToggleWindow", HotKeyService.MOD_CONTROL | HotKeyService.MOD_SHIFT, VK_SPACE);
        _hotKeyService.AddHotKey("NewIssue", HotKeyService.MOD_CONTROL | HotKeyService.MOD_ALT, VK_I);

        var modifiers = _mainWindow.ProjectHotkeyModifiers;
        var count = Math.Min(_mainWindow.ProjectShortcutCount, 9);
        for (uint i = 0; i < count; i++)
        {
            _hotKeyService.AddHotKey($"Project{i + 1}", modifiers, VK_1 + i);
        }

        _hotKeyService.Register();
    }

    private void ShowWindow()
    {
        if (_mainWindow == null) return;

        _mainWindow.Show();

        // Only restore from minimized — keep maximized if it was maximized when hidden
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void ExitApp()
    {
        if (_mainWindow != null)
        {
            // Save and shut down without hiding to tray
            _mainWindow.SaveAndClose();
        }
        Cleanup();
        Shutdown();
    }

    /// <summary>
    /// Loads the application icon using multiple strategies:
    /// 1. Managed embedded resource (GetManifestResourceStream)
    /// 2. File alongside the executable
    /// 3. Extract from the assembly's Win32 icon resource
    /// Returns null if all strategies fail.
    /// </summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        // Strategy 1: Managed embedded resource (works with PublishSingleFile)
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Reemd.icon.ico");
            if (stream != null)
                return new System.Drawing.Icon(stream);
        }
        catch
        {
            // Fall through
        }

        // Strategy 2: File alongside the executable
        try
        {
            var iconPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(iconPath))
                return new System.Drawing.Icon(iconPath);
        }
        catch
        {
            // Fall through
        }

        // Strategy 3: Extract icon from the EXE's Win32 resource (set via <ApplicationIcon>)
        // Note: Assembly.Location returns the DLL path, but the icon is on the EXE host.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null)
                    return extracted;
            }
        }
        catch
        {
            // Fall through
        }

        return null;
    }

    /// <summary>
    /// Creates a simple fallback icon programmatically when icon.ico cannot be loaded.
    /// Draws a blue circle with white "R" text.
    /// </summary>
    private static System.Drawing.Icon CreateDefaultIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);

        // Draw a filled blue circle
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(45, 45, 48));
        g.FillEllipse(brush, 0, 0, 16, 16);

        // Draw "R" letter
        using var font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
        using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        g.DrawString("R", font, textBrush, 2, 1);

        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private void Cleanup()
    {
        _hotKeyService?.Dispose();
        _trayIcon?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }
}

/// <summary>
/// Simple ICommand implementation for use with Hardcodet.NotifyIcon.Wpf.
/// </summary>
public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event System.EventHandler? CanExecuteChanged
    {
        add { System.Windows.Input.CommandManager.RequerySuggested += value; }
        remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
