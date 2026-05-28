namespace Reemd;

/// <summary>
/// Application-wide constants shared across partial files.
/// </summary>
internal static class Config
{
    /// <summary>Auto-save interval in milliseconds.</summary>
    internal const int AutoSaveIntervalMs = 5000;

    /// <summary>Preview update debounce interval in milliseconds.</summary>
    internal const int PreviewDebounceMs = 400;

    /// <summary>GitHub sync debounce interval in milliseconds.</summary>
    internal const int GitHubSyncDebounceMs = 15000;

    /// <summary>File filter for markdown files.</summary>
    internal const string MarkdownFilter = "*.md";

    /// <summary>Virtual host name used for WebView2 local file serving.</summary>
    internal const string VirtualHostName = "reemd.local";

    /// <summary>Base URL derived from <see cref="VirtualHostName"/>.</summary>
    internal static readonly string VirtualBaseUrl = $"http://{VirtualHostName}/";
}
