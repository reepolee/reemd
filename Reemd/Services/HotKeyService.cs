using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Reemd.Services;

/// <summary>
/// Registers and manages a global hotkey (Ctrl+Shift+Space) to bring the app window to the foreground.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_SPACE = 0x20;

    private readonly Window _window;
    private readonly int _hotKeyId;
    private HwndSource? _source;
    private bool _registered;

    public event Action? HotKeyPressed;

    public HotKeyService(Window window)
    {
        _window = window;
        _hotKeyId = GetHashCode();
    }

    public void Register()
    {
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        if (_source == null || _source.Handle == IntPtr.Zero)
        {
            _window.SourceInitialized += OnSourceInitialized;
            return;
        }

        RegisterHotKeyCore();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        RegisterHotKeyCore();
    }

    private void RegisterHotKeyCore()
    {
        if (_source == null) return;

        _registered = RegisterHotKey(_source.Handle, _hotKeyId, MOD_CONTROL | MOD_SHIFT, VK_SPACE);

        if (!_registered)
        {
            MessageBox.Show(
                "Failed to register global hotkey (Ctrl+Shift+Space). It may already be in use by another application.",
                "Hotkey Registration",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotKeyId)
        {
            HotKeyPressed?.Invoke();
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
        if (_registered && _source != null)
        {
            UnregisterHotKey(_source.Handle, _hotKeyId);
            _source.RemoveHook(WndProc);
            _registered = false;
        }
    }
}
