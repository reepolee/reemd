using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Reemd.Services;

/// <summary>
/// Converts raw Markdown text into an HTML document styled like VS Code's markdown preview,
/// ready to render in a WebView2 control.
/// </summary>
public sealed class MarkdownConverter
{
    // Cache for highlight.js files — read from disk once, reused for all renders
    private static string? _highlightJs;
    private static string? _githubCssLight;
    private static string? _githubCssDark;
    private static readonly object _cacheLock = new();

    // Caches inlined image data URIs keyed by file path, invalidated when the file's
    // last-write time or size changes, so repeated renders don't re-read and re-encode
    // large images on the UI thread.
    private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, string DataUri)> ImageCache = new();

    // Matches src="..." or src='...' so local image paths can be inlined as data URIs.
    private static readonly Regex ImageSrcRegex = new(
        @"src\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .Build();

    /// <summary>
    /// Converts markdown to a full HTML document with VS Code-inspired CSS styling.
    /// </summary>
    /// <param name="markdown">Raw markdown text.</param>
    /// <param name="baseFontSize">Base font size in pixels (default 14).</param>
    /// <param name="isDark">If true, applies VS Code dark theme CSS classes.</param>
    /// <param name="imageFolder">Optional folder used to resolve local image paths into inline data URIs.</param>
    public string ConvertToHtml(string markdown, double baseFontSize = 14, bool isDark = false, string? imageFolder = null)
    {
        var bodyHtml = Markdown.ToHtml(markdown ?? "", _pipeline);
        bodyHtml = InlineLocalImages(bodyHtml, imageFolder);
        var themeClass = isDark ? "vscode-dark" : "vscode-light";
        var bgColor = isDark ? "#1E1E1E" : "#FFFFFF";
        var fgColor = isDark ? "#D4D4D4" : "#333333";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html class=\"{themeClass}\" style=\"background-color: {bgColor}; color: {fgColor};\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");

        // VS Code markdown preview CSS (from the user's pasted stylesheet)
        sb.AppendLine("<style>");
        sb.AppendLine(GetVSCodeCss());
        sb.AppendLine("</style>");

        // Override font size via CSS variable
        sb.AppendLine($"<style>:root {{ --markdown-font-size: {baseFontSize}px; --markdown-line-height: {baseFontSize * 22.0 / 14.0:F1}px; }}</style>");

        // highlight.js theme and script — loaded locally and cached
        var css = isDark ? LoadHighlightCssDark() : LoadHighlightCssLight();
        if (css != null)
        {
            sb.AppendLine("<style>");
            sb.AppendLine(css);
            sb.AppendLine("</style>");
        }

        var js = LoadHighlightJs();
        if (js != null)
        {
            sb.AppendLine("<script>");
            sb.AppendLine(js);
            sb.AppendLine("</script>");
        }

        sb.AppendLine("<script>document.addEventListener('DOMContentLoaded',function(){hljs.highlightAll();});</script>");

        // Override highlight.js background so code blocks use our pre background
        sb.AppendLine("<style>");
        sb.AppendLine(".hljs { background: transparent !important; }");
        sb.AppendLine("</style>");

        // Additional spacing and typography refinements
        sb.AppendLine("<style>");
        sb.AppendLine(GetRefinementsCss(isDark));
        sb.AppendLine("</style>");

        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine(bodyHtml);
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites local (non-http/data) image src attributes into base64 data URIs so the
    /// rendered HTML is self-contained and previews identically on Windows (WebView2) and
    /// macOS (WKWebView), where NavigateToString has no reliable virtual-host/file mapping.
    /// </summary>
    private static string InlineLocalImages(string html, string? imageFolder)
    {
        if (string.IsNullOrEmpty(imageFolder))
            return html;

        return ImageSrcRegex.Replace(html, match =>
        {
            var src = match.Groups["dq"].Success ? match.Groups["dq"].Value : match.Groups["sq"].Value;
            if (IsRemoteOrDataUrl(src))
                return match.Value;

            try
            {
                var cleaned = Uri.UnescapeDataString(src.Split('?')[0].Split('#')[0]);
                var fullPath = Path.IsPathRooted(cleaned)
                    ? cleaned
                    : Path.Combine(imageFolder, cleaned.Replace('/', Path.DirectorySeparatorChar));

                var fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists)
                    return match.Value;

                var mime = GetMimeType(fileInfo.Extension);
                if (mime == null)
                    return match.Value;

                var lastWriteUtc = fileInfo.LastWriteTimeUtc;
                var length = fileInfo.Length;

                if (ImageCache.TryGetValue(fullPath, out var cached) &&
                    cached.LastWriteUtc == lastWriteUtc &&
                    cached.Length == length)
                {
                    return $"src=\"{cached.DataUri}\"";
                }

                var base64 = Convert.ToBase64String(File.ReadAllBytes(fullPath));
                var dataUri = $"data:{mime};base64,{base64}";
                ImageCache[fullPath] = (lastWriteUtc, length, dataUri);
                return $"src=\"{dataUri}\"";
            }
            catch
            {
                return match.Value;
            }
        });
    }

    private static bool IsRemoteOrDataUrl(string src)
    {
        return src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               src.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
               src.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            _ => null
        };
    }

    private static string EscapeHtmlAttr(string value)
    {
        return value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>
    /// Loads and caches highlight.min.js from embedded resources. Escapes &lt;/script&gt; to prevent
    /// premature tag closure when inlined in HTML.
    /// </summary>
    private static string? LoadHighlightJs()
    {
        if (_highlightJs != null) return _highlightJs;
        lock (_cacheLock)
        {
            if (_highlightJs != null) return _highlightJs;
            try
            {
                var content = ReadEmbeddedResource("Reemd.Resources.highlight.highlight.min.js");
                if (content != null)
                    _highlightJs = content.Replace("</script>", "<\\/script>");
            }
            catch
            {
            }
        }
        return _highlightJs;
    }

    /// <summary>
    /// Loads and caches the github (light) highlight.js theme CSS from embedded resources.
    /// </summary>
    private static string? LoadHighlightCssLight()
    {
        if (_githubCssLight != null) return _githubCssLight;
        lock (_cacheLock)
        {
            if (_githubCssLight != null) return _githubCssLight;
            try
            {
                _githubCssLight = ReadEmbeddedResource("Reemd.Resources.highlight.github.min.css");
            }
            catch
            {
            }
        }
        return _githubCssLight;
    }

    /// <summary>
    /// Loads and caches the github-dark highlight.js theme CSS from embedded resources.
    /// </summary>
    private static string? LoadHighlightCssDark()
    {
        if (_githubCssDark != null) return _githubCssDark;
        lock (_cacheLock)
        {
            if (_githubCssDark != null) return _githubCssDark;
            try
            {
                _githubCssDark = ReadEmbeddedResource("Reemd.Resources.highlight.github-dark.min.css");
            }
            catch
            {
            }
        }
        return _githubCssDark;
    }

    /// <summary>
    /// Reads an embedded resource from the assembly manifest.
    /// </summary>
    private static string? ReadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(MarkdownConverter).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetRefinementsCss(bool isDark)
    {
        var borderColor = isDark ? "rgba(255,255,255,0.06)" : "rgba(0,0,0,0.04)";
        var hoverBg = isDark ? "rgba(255,255,255,0.03)" : "rgba(0,0,0,0.02)";
        var quoteBg = isDark ? "rgba(255,255,255,0.03)" : "rgba(0,0,0,0.02)";

        return @"
/* ═══════════════════════════════════════
   Reemd preview refinements
   ═══════════════════════════════════════ */

/* Inline code — pill-like appearance, same color as code blocks */
code:not(pre code) {
	padding: 1px 6px;
	border-radius: 4px;
}

.vscode-dark code:not(pre code) {
	background-color: rgba(255,255,255,0.08);
	color: #D4D4D4;
}

.vscode-light code:not(pre code) {
	background-color: rgba(0,0,0,0.06);
	color: #333333;
}

/* Table row hover */
tbody tr:hover {
	background-color: " + hoverBg + @";
}

/* Alternating table rows */
tbody tr:nth-child(even) {
	background-color: " + borderColor + @";
}

/* Blockquote subtle background */
blockquote {
	background-color: " + quoteBg + @";
}

/* Center standalone images only */
p > img:only-child {
	display: block;
	margin: 1em auto;
}

/* Smooth scrolling */
html {
	scroll-behavior: smooth;
}

/* Better list indentation */
ul, ol {
	padding-left: 2em;
}

li {
	margin-bottom: 0.25em;
}

li > ul,
li > ol {
	margin-top: 0.25em;
	margin-bottom: 0;
}



/* Typography polish */
body {
	text-rendering: optimizeLegibility;
	-webkit-font-smoothing: antialiased;
	-moz-osx-font-smoothing: grayscale;
}

/* Keyboard shortcut styling */
kbd {
	padding: 2px 6px;
	border-radius: 3px;
	border: 1px solid;
	font-family: inherit;
	font-size: 0.85em;
}

.vscode-dark kbd {
	background-color: #2D2D2D;
	border-color: #555;
	color: #D4D4D4;
}

.vscode-light kbd {
	background-color: #F5F5F5;
	border-color: #CCC;
	color: #333;
}

/* Markdown task list checkboxes */
input[type=""checkbox""] {
	margin-right: 6px;
}
";
    }

    private static string GetVSCodeCss()
    {
        return @"/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

.vscode-dark img[src$=\#gh-light-mode-only],
.vscode-light img[src$=\#gh-dark-mode-only],
.vscode-high-contrast:not(.vscode-high-contrast-light) img[src$=\#gh-light-mode-only],
.vscode-high-contrast-light img[src$=\#gh-dark-mode-only] {
	display: none;
}

html,
body {
	font-family: var(--markdown-font-family, -apple-system, BlinkMacSystemFont, ""Segoe WPC"", ""Segoe UI"", system-ui, ""Ubuntu"", ""Droid Sans"", sans-serif);
	font-size: var(--markdown-font-size, 14px);
	padding: 0 26px;
	line-height: var(--markdown-line-height, 22px);
	word-wrap: break-word;
}

body {
	padding-top: 1em;
}

h1, h2, h3, h4, h5, h6, p, ol, ul, pre {
	margin-top: 0;
}

h1, h2, h3, h4, h5, h6 {
	font-weight: 600;
	margin-top: 24px;
	margin-bottom: 16px;
	line-height: 1.25;
}

body.scrollBeyondLastLine {
	margin-bottom: calc(100vh - 22px);
}

body.showEditorSelection .code-line {
	position: relative;
}

/* Prevent sub and sup from affecting line height */
sub, sup {
	line-height: 0;
}

ul ul:first-child, ul ol:first-child,
ol ul:first-child, ol ol:first-child {
	margin-bottom: 0;
}

img, video {
	max-width: 100%;
	max-height: 100%;
}

a {
	text-decoration: none;
}

a:hover {
	text-decoration: underline;
}

a:focus, input:focus, select:focus, textarea:focus {
	outline: 1px solid -webkit-focus-ring-color;
	outline-offset: -1px;
}

p {
	margin-bottom: 16px;
}

li p {
	margin-bottom: 0.7em;
}

ul, ol {
	margin-bottom: 0.7em;
}

hr {
	border: 0;
	height: 1px;
	border-bottom: 1px solid;
}

h1 {
	font-size: 2em;
	margin-top: 0;
	padding-bottom: 0.3em;
	border-bottom-width: 1px;
	border-bottom-style: solid;
}

h2 {
	font-size: 1.5em;
	padding-bottom: 0.3em;
	border-bottom-width: 1px;
	border-bottom-style: solid;
}

h3 { font-size: 1.25em; }
h4 { font-size: 1em; }
h5 { font-size: 0.875em; }
h6 { font-size: 0.85em; }

table {
	border-collapse: collapse;
	margin-bottom: 0.7em;
}

th {
	text-align: left;
	border-bottom: 1px solid;
}

th, td {
	padding: 5px 10px;
}

table > tbody > tr + tr > td {
	border-top: 1px solid;
}

blockquote {
	margin: 0;
	padding: 0px 16px 0 10px;
	border-left-width: 5px;
	border-left-style: solid;
	border-radius: 2px;
}

code {
	font-family: var(--vscode-editor-font-family, ""Cascadia Code"", ""SF Mono"", Monaco, Menlo, Consolas, ""Ubuntu Mono"", ""Liberation Mono"", ""DejaVu Sans Mono"", ""Courier New"", monospace);
	font-size: 1em;
	line-height: 1.357em;
}

body.wordWrap pre {
	white-space: pre-wrap;
}

pre:not(.hljs),
pre.hljs code > div {
	padding: 16px;
	border-radius: 3px;
	overflow: auto;
}

pre code {
	display: inline-block;
	color: var(--vscode-editor-foreground);
	tab-size: 4;
	background: none;
}

pre {
	background-color: var(--vscode-textCodeBlock-background);
	border: 1px solid var(--vscode-widget-border);
}

.vscode-high-contrast h1 {
	border-color: rgb(0, 0, 0);
}

.vscode-light th {
	border-color: rgba(0, 0, 0, 0.69);
}

.vscode-dark th {
	border-color: rgba(255, 255, 255, 0.69);
}

.vscode-light h1,
.vscode-light h2,
.vscode-light hr,
.vscode-light td {
	border-color: rgba(0, 0, 0, 0.18);
}

.vscode-dark h1,
.vscode-dark h2,
.vscode-dark hr,
.vscode-dark td {
	border-color: rgba(255, 255, 255, 0.18);
}

/* Override background/foreground for dark/light */
.vscode-dark {
	background-color: #1E1E1E;
	color: #D4D4D4;
}

.vscode-light {
	background-color: #FFFFFF;
	color: #333333;
}

.vscode-dark pre {
	background-color: #2D2D2D;
	border-color: #3C3C3C;
}

.vscode-light pre {
	background-color: #F5F5F5;
	border-color: #E0E0E0;
}

.vscode-dark pre code {
	background: none;
	color: #D4D4D4;
}

.vscode-light pre code {
	background: none;
	color: #333333;
}

.vscode-dark code {
	background-color: #3C3C3C;
	color: #D4D4D4;
}

.vscode-light code {
	background-color: #F0F0F0;
	color: #333333;
}

.vscode-dark blockquote {
	color: #999;
	border-left-color: #555;
}

.vscode-light blockquote {
	color: #555;
	border-left-color: #CCC;
}

/* Scrollbar styling for dark mode */
.vscode-dark ::-webkit-scrollbar {
	width: 10px;
	height: 10px;
}

.vscode-dark ::-webkit-scrollbar-thumb {
	background: #555;
	border-radius: 5px;
}

.vscode-dark ::-webkit-scrollbar-track {
	background: #1E1E1E;
}";
    }
}
