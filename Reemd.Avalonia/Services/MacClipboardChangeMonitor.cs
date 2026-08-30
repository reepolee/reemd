using System.Runtime.InteropServices;

namespace Reemd.Services;

/// <summary>
/// Reads AppKit's pasteboard change counter without fetching clipboard payload data.
/// </summary>
public sealed class MacClipboardChangeMonitor
{
    private nint? _lastChangeCount;

    public bool HasChanged()
    {
        var pasteboardClass = objc_getClass("NSPasteboard");
        var generalPasteboardSelector = sel_registerName("generalPasteboard");
        var pasteboard = objc_msgSend_intptr(pasteboardClass, generalPasteboardSelector);
        var changeCountSelector = sel_registerName("changeCount");
        var currentChangeCount = objc_msgSend_nint(pasteboard, changeCountSelector);

        var hasChanged = _lastChangeCount == null || _lastChangeCount != currentChangeCount;
        _lastChangeCount = currentChangeCount;
        return hasChanged;
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_intptr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint(IntPtr receiver, IntPtr selector);
}
