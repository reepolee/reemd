namespace Reemd.Services;

/// <summary>
/// Serializable snapshot of portable clipboard items and their representations.
/// </summary>
public sealed record ClipboardBundle(string SourcePlatform, ClipboardBundleItem[] Items)
{
    public static ClipboardBundle CreateText(string text)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        var representation = new ClipboardRepresentation("text/plain", "text", "universal", data);
        var item = new ClipboardBundleItem([representation]);
        return new ClipboardBundle(GetCurrentPlatform(), [item]);
    }

    public static string GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }
}

public sealed record ClipboardBundleItem(ClipboardRepresentation[] Representations);

public sealed record ClipboardRepresentation(
    string Identifier,
    string ValueType,
    string FormatKind,
    byte[] Data);
