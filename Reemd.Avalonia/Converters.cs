using Avalonia.Data.Converters;

namespace Reemd;

/// <summary>
/// Shared value converters used in XAML bindings.
/// </summary>
public static class Converters
{
    /// <summary>Pinned file → full opacity, unpinned → dimmed (0.25).</summary>
    public static readonly IValueConverter PinOpacity =
        new FuncValueConverter<bool, double>(isPinned => isPinned ? 1.0 : 0.25);
}
