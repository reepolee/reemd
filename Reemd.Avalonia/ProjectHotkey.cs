using Reemd.Services;

namespace Reemd;

/// <summary>
/// Available modifier combinations for the global project-launch hotkey
/// (the modifiers held while pressing a number key 1-9 to open a project).
/// </summary>
public static class ProjectHotkey
{
    public const string DefaultToken = "CtrlShift";

    /// <summary>Available combos, in display order: token, label, cross-platform modifiers.</summary>
    public static readonly (string Token, string Label, HotKeyModifiers Modifiers)[] Options =
    [
        ("CtrlShift",    "Ctrl+Shift",     HotKeyModifiers.Control | HotKeyModifiers.Shift),
        ("CtrlShiftAlt", "Ctrl+Shift+Alt", HotKeyModifiers.Control | HotKeyModifiers.Shift | HotKeyModifiers.Alt),
        ("CtrlAlt",      "Ctrl+Alt",       HotKeyModifiers.Control | HotKeyModifiers.Alt),
        ("Alt",          "Alt",            HotKeyModifiers.Alt),
        ("Ctrl",         "Ctrl",           HotKeyModifiers.Control),
        ("Shift",        "Shift",          HotKeyModifiers.Shift),
    ];

    /// <summary>Modifier flags for a token, falling back to the default combo.</summary>
    public static HotKeyModifiers ModifiersFor(string? token)
    {
        foreach (var (t, _, m) in Options)
            if (string.Equals(t, token, StringComparison.Ordinal))
                return m;
        return Options[0].Modifiers;
    }

    /// <summary>Display label for modifier flags (e.g. "Ctrl+Shift"), defaulting gracefully.</summary>
    public static string LabelFor(HotKeyModifiers modifiers)
    {
        foreach (var (_, label, m) in Options)
            if (m == modifiers)
                return label;
        return Options[0].Label;
    }

    /// <summary>Display label for a stored token.</summary>
    public static string LabelForToken(string? token) => LabelFor(ModifiersFor(token));
}
