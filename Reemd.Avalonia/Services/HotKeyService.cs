using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Reemd.Services;

/// <summary>
/// OS-agnostic modifier flags for global hotkeys. Mapped to Win32 on Windows and
/// Carbon on macOS inside <see cref="HotKeyService"/>.
/// </summary>
[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>
/// Registers and manages global (system-wide) hotkeys. On Windows this uses
/// RegisterHotKey on a hidden message-only window; on macOS it uses Carbon's
/// RegisterEventHotKey. HotKeyPressed reports which named hotkey fired.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;

    // Win32 modifier flags
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // Carbon modifier flags
    private const uint CMD_KEY = 1u << 8;      // 256
    private const uint SHIFT_KEY = 1u << 9;    // 512
    private const uint OPTION_KEY = 1u << 11;  // 2048
    private const uint CONTROL_KEY = 1u << 12; // 4096

    // macOS virtual key code (kVK_Space = 49)
    private const uint VK_SPACE = 0x31;

    private const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint kEventHotKeyPressed = 5;
    private const uint kEventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint typeEventHotKeyID = 0x686B6964; // 'hkid'
    private const uint kEventHotKeyIDSignature = 0x7265656D; // 'reem'

    public event Action<string>? HotKeyPressed;

    private readonly List<(int Id, string Name, HotKeyModifiers Modifiers, char Key)> _pending = new();
    private readonly Dictionary<int, string> _registeredNames = new();
    private int _nextId = 1;

    // Windows state (rooted to prevent GC)
    private static HotKeyService? _activeInstance;
    private static IntPtr _hwnd = IntPtr.Zero;
    private static WndProcDelegate? _wndProcDelegate;
    private static string _className = "ReemdHotKeyWindow";

    // macOS state (rooted to prevent GC)
    private static CarbonEventHandler? _carbonHandlerDelegate;
    private IntPtr _carbonEventHandlerRef = IntPtr.Zero;
    private readonly List<IntPtr> _macHotKeyRefs = new();

    /// <summary>Queues a hotkey for registration, identified by name (reported via HotKeyPressed).</summary>
    public void AddHotKey(string name, HotKeyModifiers modifiers, char key)
    {
        _pending.Add((_nextId++, name, modifiers, key));
    }

    /// <summary>
    /// Unregisters all currently-registered hotkeys and clears the pending queue,
    /// so a fresh set can be added and re-registered via Register().
    /// </summary>
    public void Reset()
    {
        UnregisterAll();
        _pending.Clear();
        _registeredNames.Clear();
        _nextId = 1;
    }

    public void Register()
    {
        if (OperatingSystem.IsWindows())
            RegisterWindows();
        else if (OperatingSystem.IsMacOS())
            RegisterMac();
    }

    #region Windows

    private void RegisterWindows()
    {
        _activeInstance = this;
        EnsureWindowsWindow();
        if (_hwnd == IntPtr.Zero) return;

        foreach (var (id, name, modifiers, key) in _pending)
        {
            var vk = key == ' ' ? 0x20u : (uint)char.ToUpperInvariant(key);
            var mods = ToWin32Modifiers(modifiers);
            if (RegisterHotKey(_hwnd, id, mods, vk))
                _registeredNames[id] = name;
        }
    }

    private static uint ToWin32Modifiers(HotKeyModifiers m)
    {
        uint result = 0;
        if ((m & HotKeyModifiers.Alt) != 0) result |= MOD_ALT;
        if ((m & HotKeyModifiers.Control) != 0) result |= MOD_CONTROL;
        if ((m & HotKeyModifiers.Shift) != 0) result |= MOD_SHIFT;
        if ((m & HotKeyModifiers.Meta) != 0) result |= MOD_WIN;
        // Stop keyboard auto-repeat from firing the hotkey repeatedly. Without this,
        // holding Ctrl+Shift+Space long enough to repeat toggles the window on and
        // then straight back off again.
        if (result != 0) result |= MOD_NOREPEAT;
        return result;
    }

    private static void EnsureWindowsWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };

        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, _className, null, 0, 0, 0, 0, 0,
            new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_activeInstance != null &&
                _activeInstance._registeredNames.TryGetValue(id, out var name))
            {
                _activeInstance.HotKeyPressed?.Invoke(name);
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    #endregion

    #region macOS (Carbon)

    private void RegisterMac()
    {
        EnsureMacHandler();

        var target = GetApplicationEventTarget();
        foreach (var (id, name, modifiers, key) in _pending)
        {
            var kk = ToMacKeyCode(key);
            var mods = ToMacModifiers(modifiers);

            var hotKeyId = new EventHotKeyID { signature = kEventHotKeyIDSignature, id = (uint)id };
            var status = RegisterEventHotKey(kk, mods, hotKeyId, target, 0, out var hotKeyRef);
            if (status == 0 && hotKeyRef != IntPtr.Zero)
            {
                _registeredNames[id] = name;
                _macHotKeyRefs.Add(hotKeyRef);
            }
            else
            {
                Console.Error.WriteLine($"[Reemd] RegisterEventHotKey '{name}' failed (key=0x{kk:X}, mods=0x{mods:X}, status={status})");
            }
        }
    }

    /// <summary>
    /// Maps a key character to its macOS virtual key code (kVK_*). The kVK_ANSI_* values
    /// follow physical key order on an ANSI keyboard, NOT alphabetical/sequential order,
    /// so letters and digits each need an explicit table.
    /// </summary>
    private static uint ToMacKeyCode(char key)
    {
        if (key == ' ') return VK_SPACE;

        return char.ToUpperInvariant(key) switch
        {
            // Letters (kVK_ANSI_* in physical QWERTY order)
            'A' => 0x00, 'B' => 0x0B, 'C' => 0x08, 'D' => 0x02, 'E' => 0x0E,
            'F' => 0x03, 'G' => 0x05, 'H' => 0x04, 'I' => 0x22, 'J' => 0x26,
            'K' => 0x28, 'L' => 0x25, 'M' => 0x2E, 'N' => 0x2D, 'O' => 0x1F,
            'P' => 0x23, 'Q' => 0x0C, 'R' => 0x0F, 'S' => 0x01, 'T' => 0x11,
            'U' => 0x20, 'V' => 0x09, 'W' => 0x0D, 'X' => 0x07, 'Y' => 0x10,
            'Z' => 0x06,
            // Digits (kVK_ANSI_* in physical order: 1,2,3,4,6,5,9,7,8,0)
            '1' => 0x12, '2' => 0x13, '3' => 0x14, '4' => 0x15, '5' => 0x17,
            '6' => 0x16, '7' => 0x1A, '8' => 0x1C, '9' => 0x19, '0' => 0x1D,
            _ => VK_SPACE,
        };
    }

    private static uint ToMacModifiers(HotKeyModifiers m)
    {
        uint result = 0;
        if ((m & HotKeyModifiers.Alt) != 0) result |= OPTION_KEY;
        if ((m & HotKeyModifiers.Control) != 0) result |= CONTROL_KEY;
        if ((m & HotKeyModifiers.Shift) != 0) result |= SHIFT_KEY;
        if ((m & HotKeyModifiers.Meta) != 0) result |= CMD_KEY;
        return result;
    }

    private void EnsureMacHandler()
    {
        if (_carbonHandlerDelegate != null) return;

        _activeInstance = this;
        _carbonHandlerDelegate = CarbonHandler;

        var eventType = new EventTypeSpec { eventClass = kEventClassKeyboard, eventKind = kEventHotKeyPressed };
        var types = new[] { eventType };

        var status = InstallEventHandler(GetApplicationEventTarget(), _carbonHandlerDelegate, 1, types, IntPtr.Zero, out _carbonEventHandlerRef);
        if (status != 0)
            Console.Error.WriteLine($"[Reemd] InstallEventHandler failed (status={status})");
    }

    private static int CarbonHandler(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        try
        {
            var hotKeyId = default(EventHotKeyID);
            var status = GetEventParameter(theEvent, kEventParamDirectObject, typeEventHotKeyID, IntPtr.Zero,
                (uint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, ref hotKeyId);
            if (status != 0) return 0;

            var id = (int)hotKeyId.id;
            var instance = _activeInstance;
            if (instance != null && instance._registeredNames.TryGetValue(id, out var name))
            {
                var captured = name;
                Dispatcher.UIThread.Post(() => instance.HotKeyPressed?.Invoke(captured));
            }
        }
        catch
        {
            // Best-effort — never crash the run loop
        }
        return 0; // noErr
    }

    #endregion

    private void UnregisterAll()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_hwnd != IntPtr.Zero)
            {
                foreach (var id in _registeredNames.Keys)
                    UnregisterHotKey(_hwnd, id);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var hotKeyRef in _macHotKeyRefs)
            {
                if (hotKeyRef != IntPtr.Zero)
                    UnregisterEventHotKey(hotKeyRef);
            }
            _macHotKeyRefs.Clear();
        }
    }

    public void Dispose()
    {
        UnregisterAll();

        if (OperatingSystem.IsWindows())
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (_carbonEventHandlerRef != IntPtr.Zero)
            {
                RemoveEventHandler(_carbonEventHandlerRef);
                _carbonEventHandlerRef = IntPtr.Zero;
            }
        }

        _registeredNames.Clear();
        _pending.Clear();
    }

    #region P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate int CarbonEventHandler(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint eventClass;
        public uint eventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint signature;
        public uint id;
    }

    [DllImport(CarbonLib)]
    private static extern int RegisterEventHotKey(uint inHotKeyCode, uint inHotKeyModifiers,
        EventHotKeyID inHotKeyID, IntPtr inTarget, int inOptions, out IntPtr outRef);

    [DllImport(CarbonLib)]
    private static extern int UnregisterEventHotKey(IntPtr inHotKeyRef);

    [DllImport(CarbonLib)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLib)]
    private static extern int InstallEventHandler(IntPtr inTarget, CarbonEventHandler inHandler,
        uint inNumTypes, EventTypeSpec[] inList, IntPtr userData, out IntPtr outRef);

    [DllImport(CarbonLib)]
    private static extern int RemoveEventHandler(IntPtr inHandlerRef);

    [DllImport(CarbonLib)]
    private static extern int GetEventParameter(IntPtr inEvent, uint inName, uint inDesiredType,
        IntPtr outActualType, uint inBufferSize, IntPtr outActualSize, ref EventHotKeyID outData);

    #endregion
}
