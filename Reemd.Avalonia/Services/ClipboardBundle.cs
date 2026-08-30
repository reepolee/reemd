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

    public int GetByteCount()
    {
        var byte_count = 0;
        foreach (var item in Items)
        {
            foreach (var representation in item.Representations)
                byte_count += representation.Data.Length;
        }

        return byte_count;
    }

    public string DescribeFormats()
    {
        var format_descriptions = new List<string>();

        for (var item_index = 0; item_index < Items.Length; item_index++)
        {
            var item = Items[item_index];
            foreach (var representation in item.Representations)
            {
                var identifier = representation.Identifier.Replace('\r', ' ');
                identifier = identifier.Replace('\n', ' ');
                identifier = identifier.Replace('\t', ' ');
                format_descriptions.Add(
                    $"item={item_index + 1}, identifier={identifier}, kind={representation.FormatKind}, " +
                    $"type={representation.ValueType}, bytes={representation.Data.Length}");
            }
        }

        return string.Join("; ", format_descriptions);
    }
}

public sealed record ClipboardBundleItem(ClipboardRepresentation[] Representations);

public sealed record ClipboardRepresentation(
    string Identifier,
    string ValueType,
    string FormatKind,
    byte[] Data);
