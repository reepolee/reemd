using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Reemd.Dialogs;
using Reemd.Services;

namespace Reemd;

/// <summary>
/// Application entry point. Manages the system tray icon and global hotkeys.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private HotKeyService? _hotKeyService;
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        // Shown in the macOS app menu / Dock and other OS chrome.
        Name = "ReeMD";
        AvaloniaXamlLoader.Load(this);
        SetupAppMenu();
    }

    /// <summary>
    /// Defines the macOS application menu. Without this, Avalonia shows a default
    /// "About Avalonia" item; defining our own brands it as ReeMD and lets us show
    /// a custom About dialog. Avalonia appends the standard "Quit ReeMD" item.
    /// </summary>
    private void SetupAppMenu()
    {
        var menu = new NativeMenu();

        var aboutItem = new NativeMenuItem { Header = "About ReeMD..." };
        aboutItem.Click += (_, _) => ShowAboutDialog();
        menu.Items.Add(aboutItem);

        NativeMenu.SetMenu(this, menu);
    }

    private void ShowAboutDialog()
    {
        var dialog = new AboutDialog(_mainWindow?.IsDarkMode ?? false);

        if (_mainWindow != null)
        {
            dialog.ShowDialog(_mainWindow);
        }
        else
        {
            dialog.Show();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // First command-line argument = startup folder, if provided.
            string? startupFolder = desktop.Args is { Length: > 0 } args ? args[0] : null;

            _mainWindow = new MainWindow(startupFolder);

            SetupTrayIcon();

            _hotKeyService = new HotKeyService();
            _hotKeyService.HotKeyPressed += OnHotKeyPressed;

            // Re-register hotkeys whenever the project list changes, so the global
            // Ctrl+Shift+N keys always match the current project count.
            _mainWindow.ProjectShortcutsChanged += RegisterHotKeys;

            RegisterHotKeys();

            desktop.MainWindow = _mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TrayIcon
        {
            ToolTipText = "ReeMD - Markdown Editor",
            IsVisible = true
        };

        var icon = LoadWindowIcon();
        if (icon != null)
        {
            _trayIcon.Icon = icon;
            if (_mainWindow != null)
                _mainWindow.Icon = icon;
        }

        var showItem = new NativeMenuItem { Header = "Show" };
        showItem.Click += (_, _) => ShowWindow();

        var exitItem = new NativeMenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApp();

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.Menu = menu;

        // Click (macOS) or double-click (Windows) to show.
        _trayIcon.Clicked += (_, _) => ShowWindow();
    }

    private static WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("Reemd.icon.ico");
            if (stream != null)
                return new WindowIcon(stream);
        }
        catch
        {
            // Best-effort — fall back to the default icon.
        }
        return null;
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

        _hotKeyService.AddHotKey("ToggleWindow", HotKeyModifiers.Control | HotKeyModifiers.Shift, ' ');
        _hotKeyService.AddHotKey("NewIssue", HotKeyModifiers.Control | HotKeyModifiers.Alt, 'I');

        var modifiers = _mainWindow.ProjectHotkeyModifiers;
        var count = Math.Min(_mainWindow.ProjectShortcutCount, 9);
        for (var i = 0; i < count; i++)
        {
            _hotKeyService.AddHotKey($"Project{i + 1}", modifiers, (char)('1' + i));
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
        _mainWindow.FocusEditor();
    }

    private void ExitApp()
    {
        _mainWindow?.SaveAndClose();

        Cleanup();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void Cleanup()
    {
        _hotKeyService?.Dispose();
        _trayIcon?.Dispose();
    }
}
