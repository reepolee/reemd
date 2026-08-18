using Reemd.Services;

namespace Reemd;

/// <summary>
/// Available modifier combinations for the global project-launch hotkey
/// (the modifiers held while pressing a number key 1-9 to open a project).
/// </summary>
public static class ProjectHotkey
{
    public const string DefaultToken = "CtrlShift";

    /// <summary>Available combos, in display order: token, label, Win32 modifiers.</summary>
    public static readonly (string Token, string Label, uint Modifiers)[] Options =
    [
        ("CtrlShift",    "Ctrl+Shift",     HotKeyService.MOD_CONTROL | HotKeyService.MOD_SHIFT),
        ("CtrlShiftAlt", "Ctrl+Shift+Alt", HotKeyService.MOD_CONTROL | HotKeyService.MOD_SHIFT | HotKeyService.MOD_ALT),
        ("CtrlAlt",      "Ctrl+Alt",       HotKeyService.MOD_CONTROL | HotKeyService.MOD_ALT),
        ("Alt",          "Alt",            HotKeyService.MOD_ALT),
        ("Ctrl",         "Ctrl",           HotKeyService.MOD_CONTROL),
        ("Shift",        "Shift",          HotKeyService.MOD_SHIFT),
    ];

    /// <summary>Win32 modifier flags for a token, falling back to the default combo.</summary>
    public static uint ModifiersFor(string? token)
    {
        foreach (var (t, _, m) in Options)
            if (string.Equals(t, token, StringComparison.Ordinal))
                return m;
        return Options[0].Modifiers;
    }

    /// <summary>Display label for modifier flags (e.g. "Ctrl+Shift"), defaulting gracefully.</summary>
    public static string LabelFor(uint modifiers)
    {
        foreach (var (_, label, m) in Options)
            if (m == modifiers)
                return label;
        return Options[0].Label;
    }

    /// <summary>Display label for a stored token.</summary>
    public static string LabelForToken(string? token) => LabelFor(ModifiersFor(token));
}
