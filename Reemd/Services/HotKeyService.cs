using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Reemd.Services;

/// <summary>
/// Registers and manages global (system-wide) hotkeys via RegisterHotKey/WM_HOTKEY.
/// Multiple hotkeys can be registered; HotKeyPressed reports which one fired.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    private readonly Window _window;
    private readonly List<(int Id, string Name, uint Modifiers, uint VirtualKey)> _pendingHotKeys = new();
    private readonly Dictionary<int, string> _registeredNames = new();
    private HwndSource? _source;
    private int _nextId = 1;
    private bool _hookAdded;

    public event Action<string>? HotKeyPressed;

    public HotKeyService(Window window)
    {
        _window = window;
    }

    /// <summary>
    /// Queues a hotkey for registration, identified by name (reported via HotKeyPressed).
    /// Must be called before Register().
    /// </summary>
    public void AddHotKey(string name, uint modifiers, uint virtualKey)
    {
        _pendingHotKeys.Add((_nextId++, name, modifiers, virtualKey));
    }

    /// <summary>
    /// Unregisters all currently-registered hotkeys and clears the pending queue,
    /// so a fresh set can be added and re-registered via Register().
    /// </summary>
    public void Reset()
    {
        if (_source != null)
        {
            foreach (var id in _registeredNames.Keys)
                UnregisterHotKey(_source.Handle, id);
        }
        _registeredNames.Clear();
        _pendingHotKeys.Clear();
        _nextId = 1;
    }

    public void Register()
    {
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        if (_source == null || _source.Handle == IntPtr.Zero)
        {
            _window.SourceInitialized += OnSourceInitialized;
            return;
        }

        RegisterHotKeysCore();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        RegisterHotKeysCore();
    }

    private void RegisterHotKeysCore()
    {
        if (_source == null) return;

        foreach (var (id, name, modifiers, virtualKey) in _pendingHotKeys)
        {
            var registered = RegisterHotKey(_source.Handle, id, modifiers, virtualKey);
            if (registered)
            {
                _registeredNames[id] = name;
            }
            else
            {
                MessageBox.Show(
                    $"Failed to register global hotkey \"{name}\". It may already be in use by another application.",
                    "Hotkey Registration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        if (!_hookAdded)
        {
            _source.AddHook(WndProc);
            _hookAdded = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registeredNames.TryGetValue(wParam.ToInt32(), out var name))
        {
            HotKeyPressed?.Invoke(name);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        if (_source != null)
        {
            foreach (var id in _registeredNames.Keys)
                UnregisterHotKey(_source.Handle, id);
            _source.RemoveHook(WndProc);
            _registeredNames.Clear();
        }
        _hookAdded = false;
    }
}
