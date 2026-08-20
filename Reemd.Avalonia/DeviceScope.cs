namespace Reemd;

/// <summary>
/// Machine + OS identity used to scope per-device settings files inside a shared
/// git repo, so each device keeps its own copy (e.g. <c>comet.win.reemd.projects.json</c>,
/// <c>m4mini.macos.reemd.projects.json</c>) without clobbering the others. A plain
/// root-level filename (no dot-prefix, no subfolder) keeps it out of reach of
/// common .gitignore patterns like <c>.*</c> or <c>.reemd/</c>.
/// </summary>
public static class DeviceScope
{
    /// <summary>Lowercased, filesystem-safe machine name (e.g. "comet", "m4mini").</summary>
    public static string DeviceName { get; } = Sanitize(Environment.MachineName);

    /// <summary>Short OS token used in per-device filenames: "win", "macos", "linux".</summary>
    public static string PlatformToken { get; } =
        OperatingSystem.IsWindows() ? "win" :
        OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>
    /// Builds a device-scoped filename for the given settings name, e.g.
    /// <c>FileName("reemd.projects")</c> → "comet.win.reemd.projects.json".
    /// </summary>
    public static string FileName(string settingsName) =>
        $"{DeviceName}.{PlatformToken}.{settingsName}.json";

    /// <summary>Trims a machine name to filename-safe characters, lowercased.</summary>
    private static string Sanitize(string? name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value)) return "unknown";

        // macOS hostnames can carry a ".local" suffix.
        if (value.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            value = value[..^".local".Length];

        var cleaned = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned.ToLowerInvariant();
    }
}
