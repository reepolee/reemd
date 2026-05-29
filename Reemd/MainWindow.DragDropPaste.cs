using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Reemd;

/// <summary>
/// Partial class containing drag & drop and clipboard paste logic for handling
/// image files, image URLs, and HTML image drags.
/// </summary>
public partial class MainWindow
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico"
    };

    #region Drag & Drop

    /// <summary>
    /// Shows a copy cursor when dragging image files or text over the editor.
    /// Handles the event so our PreviewDrop handler can decide how to insert
    /// (markdown image syntax for image URLs, plain text otherwise).
    /// </summary>
    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool handled = false;

        // Check for image file drops
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 &&
                files.Any(f => ImageExtensions.Contains(Path.GetExtension(f))))
                handled = true;
        }

        // Check for any text data (URLs, links, etc.) — try multiple formats
        // typeof(string) covers in-app drag/drop, UnicodeText/Text covers cross-app (browser) drops
        // Html covers dragging images from web pages (the URL is embedded in <img> markup)
        if (!handled && (e.Data.GetDataPresent(typeof(string)) ||
                          e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                          e.Data.GetDataPresent(DataFormats.Text) ||
                          e.Data.GetDataPresent(DataFormats.Html) ||
                          e.Data.GetDataPresent("UniformResourceLocatorW") ||
                          e.Data.GetDataPresent("UniformResourceLocator")))
            handled = true;

        if (!handled) return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    /// Extracts a string from drag data, handling both direct strings and MemoryStream
    /// results from OLE (cross-app) drag sources like browsers.
    /// </summary>
    private static string? GetTextFromDragData(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format)) return null;

        var raw = data.GetData(format);
        if (raw == null) return null;

        if (raw is string s)
            return s;

        if (raw is MemoryStream ms)
        {
            // UnicodeText = UTF-16 LE, Text = system default ANSI
            var encoding = format == DataFormats.UnicodeText
                ? Encoding.Unicode
                : Encoding.Default;
            return encoding.GetString(ms.ToArray()).TrimEnd('\0');
        }

        // Last resort: try ToString
        return raw.ToString();
    }

    /// <summary>
    /// Reads a URL from the Windows shell UniformResourceLocatorW (UTF-16) or
    /// UniformResourceLocator (ANSI) clipboard formats, used by Chrome and Edge
    /// when dragging from the address bar or a hyperlink.
    /// </summary>
    private static string? GetWideCharUrlFromDragData(IDataObject data)
    {
        if (data.GetDataPresent("UniformResourceLocatorW"))
        {
            var raw = data.GetData("UniformResourceLocatorW");
            if (raw is string s) return s;
            if (raw is MemoryStream ms) return Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
        }
        if (data.GetDataPresent("UniformResourceLocator"))
        {
            var raw = data.GetData("UniformResourceLocator");
            if (raw is string s) return s;
            if (raw is MemoryStream ms) return Encoding.Default.GetString(ms.ToArray()).TrimEnd('\0');
        }
        return null;
    }

    /// <summary>
    /// Parses HTML drag data (e.g. from dragging an image out of a browser) and extracts
    /// any image source URLs found in &lt;img&gt; tags or &lt;a href&gt; links.
    /// The HTML format often contains header metadata before the actual fragment.
    /// </summary>
    private static List<string> GetImageUrlsFromHtml(IDataObject data)
    {
        var urls = new List<string>();

        var raw = GetTextFromDragData(data, DataFormats.Html);
        if (string.IsNullOrEmpty(raw)) return urls;

        // Strip the CF_HTML header (contains Version, StartHTML, EndHTML, etc.) to get the actual HTML fragment
        // The fragment starts after <!--StartFragment--> and ends at <!--EndFragment-->
        var fragmentStart = raw.IndexOf("<!--StartFragment-->", StringComparison.Ordinal);
        var fragmentEnd = raw.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);

        string html;
        if (fragmentStart >= 0 && fragmentEnd > fragmentStart)
        {
            html = raw.Substring(fragmentStart + "<!--StartFragment-->".Length,
                                 fragmentEnd - fragmentStart - "<!--StartFragment-->".Length);
        }
        else
        {
            html = raw;
        }

        // Find all <img src="..."> or <img src='...'>
        int idx = 0;
        while ((idx = html.IndexOf("<img", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var srcEnd = html.IndexOf(">", idx, StringComparison.OrdinalIgnoreCase);
            if (srcEnd < 0) break;

            var tag = html.Substring(idx, srcEnd - idx + 1);

            // Match src="..." or src='...'
            var srcMatch = System.Text.RegularExpressions.Regex.Match(tag,
                @"src\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (srcMatch.Success)
            {
                var src = srcMatch.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(src) && IsImageUrl(src))
                    urls.Add(src);
            }

            idx = srcEnd + 1;
        }

        // Also check <a href="..."> tags — Chrome link drags provide href, not img src
        int aIdx = 0;
        while ((aIdx = html.IndexOf("<a ", aIdx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var tagEnd = html.IndexOf(">", aIdx, StringComparison.OrdinalIgnoreCase);
            if (tagEnd < 0) break;

            var tag = html.Substring(aIdx, tagEnd - aIdx + 1);
            var hrefMatch = System.Text.RegularExpressions.Regex.Match(tag,
                @"href\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (hrefMatch.Success)
            {
                var href = hrefMatch.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(href) && IsImageUrl(href) && !urls.Contains(href))
                    urls.Add(href);
            }

            aIdx = tagEnd + 1;
        }

        return urls;
    }

    /// <summary>
    /// Handles dropping image URLs or image files onto the editor (tunnel phase).
    /// Fires before the TextBox's built-in drop handler, so we can intercept
    /// image URLs and insert markdown instead of plain text.
    /// Inserts markdown image syntax ![alt](url) at the drop position.
    /// </summary>
    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        var dropIndex = Editor.GetCharacterIndexFromPoint(e.GetPosition(Editor), true);
        if (dropIndex < 0) dropIndex = Editor.Text.Length;

        // First, try to extract any text from the drag data (URLs, etc.)
        // Do this BEFORE checking FileDrop so we can determine if it's an image URL
        string? text = GetTextFromDragData(e.Data, typeof(string).FullName!)
                    ?? GetTextFromDragData(e.Data, DataFormats.UnicodeText)
                    ?? GetTextFromDragData(e.Data, DataFormats.Text)
                    ?? GetWideCharUrlFromDragData(e.Data);

        // Handle file drops
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];

            if (files != null && files.Length > 0)
            {
                var imageFiles = files.Where(f => ImageExtensions.Contains(Path.GetExtension(f))).ToList();

                if (imageFiles.Count > 0)
                {
                    // Insert each image as markdown (file copy path)
                    var lines = new List<string>();
                    foreach (var file in imageFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var destPath = Path.Combine(_markdownFolder, fileName);

                        string imagePath;
                        if (string.Equals(file, destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            imagePath = MakeRelativePath(_markdownFolder, file).Replace('\\', '/');
                        }
                        else
                        {
                            if (!File.Exists(destPath))
                            {
                                try { File.Copy(file, destPath); } catch { }
                            }
                            imagePath = fileName;
                        }

                        lines.Add($"![{Path.GetFileNameWithoutExtension(fileName)}]({imagePath})");
                    }

                    var insertion = string.Join("\n", lines) + "\n";
                    Editor.Text = Editor.Text.Insert(dropIndex, insertion);
                    Editor.CaretIndex = dropIndex + insertion.Length;
                    SetStatus($"Inserted {imageFiles.Count} image(s)");
                    e.Handled = true;
                    return;
                }

                // FileDrop present but no image files — fall through to text handling below
            }
        }

        // Handle text/URL drops (also reached from FileDrop with no image files)
        // Chrome may provide "URL\nTitle" in UnicodeText — take only the first line.
        string? textToInsert = !string.IsNullOrWhiteSpace(text)
            ? text.Split('\n')[0].Trim()
            : null;

        if (textToInsert != null && IsImageUrl(textToInsert))
        {
            var markdown = $"![Image]({textToInsert})";
            Editor.Text = Editor.Text.Insert(dropIndex, markdown);
            Editor.CaretIndex = dropIndex + markdown.Length;
            SetStatus("Inserted image from URL");
            e.Handled = true;
            return;
        }

        // Check if browser provided the URL via HTML format
        // (dragging an image from a web page often puts the URL only in DataFormats.Html)
        // This runs even when text was null (e.g. UniformResourceLocatorW wasn't checked).
        var htmlImageUrls = GetImageUrlsFromHtml(e.Data);
        if (htmlImageUrls.Count > 0)
        {
            var insertion = string.Join("\n", htmlImageUrls.Select(u => $"![Image]({u})"));
            Editor.Text = Editor.Text.Insert(dropIndex, insertion);
            Editor.CaretIndex = dropIndex + insertion.Length;
            SetStatus($"Inserted {htmlImageUrls.Count} image(s) from HTML");
            e.Handled = true;
            return;
        }

        // Non-image text — insert as plain text at drop position
        if (textToInsert != null)
        {
            Editor.Text = Editor.Text.Insert(dropIndex, textToInsert);
            Editor.CaretIndex = dropIndex + textToInsert.Length;
            SetStatus("Dropped text");
            e.Handled = true;
        }
    }

    /// <summary>
    /// Checks whether a URL (or plain text) looks like an image URL based on file extension.
    /// Strips query parameters and any newline-appended title text before checking the extension.
    /// </summary>
    private static bool IsImageUrl(string text)
    {
        text = text.Trim().Trim('"', '\'');

        // Browsers sometimes append a page title after a newline (e.g. "url.png\nTitle")
        // which would corrupt Path.GetExtension — take only the first line.
        var firstLine = text.Split('\n')[0].Split('\r')[0].Trim();
        text = firstLine;

        // Strip query string and fragment for extension check
        var cleanPath = text.Split('?')[0].Split('#')[0];

        // Must look like a URL or path
        if (!text.StartsWith("http://") && !text.StartsWith("https://") &&
            !text.StartsWith("ftp://") && !Path.HasExtension(cleanPath))
            return false;

        var ext = Path.GetExtension(cleanPath);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    /// <summary>
    /// Creates a relative path from base path to target path.
    /// </summary>
    private static string MakeRelativePath(string basePath, string targetPath)
    {
        if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            basePath += Path.DirectorySeparatorChar;

        var baseUri = new Uri(basePath);
        var targetUri = new Uri(targetPath);
        var relative = baseUri.MakeRelativeUri(targetUri).ToString();
        return Uri.UnescapeDataString(relative);
    }

    #endregion

    #region Image Paste from Clipboard

    /// <summary>
    /// Guards against re-entering HandlePaste when we invoke Editor.Paste()
    /// for the default paste fallback.
    /// </summary>
    private bool _isDefaultPasting;

    /// <summary>
    /// Handles paste at the command-binding level — catches Ctrl+V, right-click paste,
    /// and Shift+Insert. Checks clipboard for:
    ///   1. An image bitmap → saves to markdown folder, inserts ![filename](path)
    ///   2. Text that looks like an image URL → inserts ![Image](url)
    /// If neither matches, invokes the TextBox's native Paste() to insert clipboard text.
    /// </summary>
    private void HandlePaste(ExecutedRoutedEventArgs e)
    {
        // Prevent recursion if Editor.Paste() somehow re-triggers us
        if (_isDefaultPasting) return;

        // Check 1: clipboard has actual image bitmap
        if (Clipboard.ContainsImage())
        {
            try
            {
                var bitmapSource = Clipboard.GetImage();
                if (bitmapSource == null) return;

                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var fileName = $"image-{timestamp}.png";
                var filePath = Path.Combine(_markdownFolder, fileName);

                int counter = 1;
                while (File.Exists(filePath))
                {
                    fileName = $"image-{timestamp}-{counter}.png";
                    filePath = Path.Combine(_markdownFolder, fileName);
                    counter++;
                }

                var dir = Path.GetDirectoryName(filePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fileStream = new FileStream(filePath, FileMode.Create);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(fileStream);

                var markdown = $"![{Path.GetFileNameWithoutExtension(fileName)}]({fileName})";
                var caretIndex = Editor.CaretIndex;
                Editor.Text = Editor.Text.Insert(caretIndex, markdown);
                Editor.CaretIndex = caretIndex + markdown.Length;

                _isLoadingDocument = true;
                RefreshFileList();
                _isLoadingDocument = false;

                SetStatus($"Pasted image: {fileName}");
                e.Handled = true;
                return;
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to paste image: {ex.Message}");
                return;
            }
        }

        // Check 2: clipboard has text that looks like an image URL
        if (Clipboard.ContainsText())
        {
            try
            {
                var text = Clipboard.GetText().Trim();
                if (!string.IsNullOrWhiteSpace(text) && IsImageUrl(text))
                {
                    var markdown = $"![Image]({text})";
                    var caretIndex = Editor.CaretIndex;
                    Editor.Text = Editor.Text.Insert(caretIndex, markdown);
                    Editor.CaretIndex = caretIndex + markdown.Length;
                    SetStatus("Pasted image URL");
                    e.Handled = true;
                    return;
                }
            }
            catch
            {
                // Best-effort
            }
        }

        // Neither image nor image URL — invoke the TextBox's native paste behavior.
        // Our CommandBinding takes priority over the TextBox's internal paste handler,
        // so simply not setting e.Handled does NOT cause the default paste to fire.
        // We must explicitly call Editor.Paste() (which accesses the clipboard directly
        // without going through command routing) and then mark the event handled.
        _isDefaultPasting = true;
        try
        {
            Editor.Paste();
            e.Handled = true;
        }
        catch
        {
            // Best-effort – if Paste() fails, there's nothing else to fall back to
        }
        finally
        {
            _isDefaultPasting = false;
        }
    }

    #endregion
}

