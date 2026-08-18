using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Reemd;

/// <summary>
/// Partial class containing drag & drop and clipboard paste logic for handling
/// image files, image URLs, and text.
/// </summary>
public partial class MainWindow
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico"
    };

    #region Drag & Drop

    private void Editor_DragOver(object? sender, DragEventArgs e)
    {
        bool handled = false;

        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files != null && files.Count > 0 &&
            files.Any(f => ImageExtensions.Contains(Path.GetExtension(GetLocalPath(f)))))
        {
            handled = true;
        }

        if (!handled && !string.IsNullOrEmpty(e.DataTransfer.TryGetText()))
            handled = true;

        if (!handled) return;

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static string GetLocalPath(IStorageItem item)
    {
        try
        {
            return item.TryGetLocalPath() ?? item.Name;
        }
        catch
        {
            return item.Name;
        }
    }

    private void Editor_Drop(object? sender, DragEventArgs e)
    {
        var text = Editor.Text ?? string.Empty;
        int dropIndex = Math.Clamp(Editor.CaretIndex, 0, text.Length);

        // Handle file drops first
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files != null && files.Count > 0)
        {
            var imageFiles = files
                .Select(GetLocalPath)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (imageFiles.Count > 0)
            {
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
                Editor.Text = text.Insert(dropIndex, insertion);
                Editor.CaretIndex = dropIndex + insertion.Length;
                SetStatus($"Inserted {imageFiles.Count} image(s)");
                e.Handled = true;
                return;
            }
        }

        // Handle text/URL drops
        var droppedText = e.DataTransfer.TryGetText();
        var textToInsert = !string.IsNullOrWhiteSpace(droppedText)
            ? droppedText.Split('\n')[0].Trim()
            : null;

        if (textToInsert != null && IsImageUrl(textToInsert))
        {
            var markdown = $"![Image]({textToInsert})";
            Editor.Text = text.Insert(dropIndex, markdown);
            Editor.CaretIndex = dropIndex + markdown.Length;
            SetStatus("Inserted image from URL");
            e.Handled = true;
            return;
        }

        // Non-image text — insert as plain text at drop position
        if (textToInsert != null)
        {
            Editor.Text = text.Insert(dropIndex, textToInsert);
            Editor.CaretIndex = dropIndex + textToInsert.Length;
            SetStatus("Dropped text");
            e.Handled = true;
        }
    }

    /// <summary>
    /// Checks whether a URL (or plain text) looks like an image URL based on file extension.
    /// </summary>
    private static bool IsImageUrl(string text)
    {
        text = text.Trim().Trim('"', '\'');

        var firstLine = text.Split('\n')[0].Split('\r')[0].Trim();
        text = firstLine;

        var cleanPath = text.Split('?')[0].Split('#')[0];

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
    /// Handles paste — catches Ctrl+V, Shift+Insert, and the context-menu Paste.
    /// Checks the clipboard for (1) a bitmap image, (2) text that looks like an image
    /// URL, (3) image files. Falls back to the TextBox's native paste otherwise.
    /// </summary>
    private async void HandlePaste()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            Editor.Paste();
            return;
        }

        IAsyncDataTransfer? data = null;
        try
        {
            data = await clipboard.TryGetDataAsync();
            if (data != null)
            {
                // Check 1: raw bitmap image on the clipboard
                var bitmap = await data.TryGetBitmapAsync();
                if (bitmap != null)
                {
                    var saved = await SavePastedBitmapAsync(bitmap);
                    if (saved != null)
                    {
                        var markdown = $"![{Path.GetFileNameWithoutExtension(saved)}]({saved})";
                        var caretIndex = Editor.CaretIndex;
                        var text = Editor.Text ?? string.Empty;
                        Editor.Text = text.Insert(caretIndex, markdown);
                        Editor.CaretIndex = caretIndex + markdown.Length;

                        _isLoadingDocument = true;
                        RefreshFileList();
                        _isLoadingDocument = false;

                        SetStatus($"Pasted image: {saved}");
                        return;
                    }
                }

                // Check 2: clipboard has text that looks like an image URL
                var textFromClipboard = await data.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(textFromClipboard) && IsImageUrl(textFromClipboard.Trim()))
                {
                    var markdown = $"![Image]({textFromClipboard.Trim()})";
                    var caretIndex = Editor.CaretIndex;
                    var text = Editor.Text ?? string.Empty;
                    Editor.Text = text.Insert(caretIndex, markdown);
                    Editor.CaretIndex = caretIndex + markdown.Length;
                    SetStatus("Pasted image URL");
                    return;
                }

                // Check 3: clipboard has image files
                var files = await data.TryGetFilesAsync();
                if (files != null)
                {
                    var imageFiles = files
                        .Select(GetLocalPath)
                        .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                        .ToList();
                    if (imageFiles.Count > 0)
                    {
                        var lines = new List<string>();
                        foreach (var file in imageFiles)
                        {
                            var fileName = Path.GetFileName(file);
                            var destPath = Path.Combine(_markdownFolder, fileName);
                            if (!File.Exists(destPath) && File.Exists(file))
                            {
                                try { File.Copy(file, destPath); } catch { }
                            }
                            lines.Add($"![{Path.GetFileNameWithoutExtension(fileName)}]({fileName})");
                        }
                        var insertion = string.Join("\n", lines);
                        var caretIndex = Editor.CaretIndex;
                        var text = Editor.Text ?? string.Empty;
                        Editor.Text = text.Insert(caretIndex, insertion);
                        Editor.CaretIndex = caretIndex + insertion.Length;
                        SetStatus($"Pasted {imageFiles.Count} image file(s)");
                        return;
                    }
                }
            }
        }
        catch
        {
            // Best-effort — fall through to native paste
        }
        finally
        {
            data?.Dispose();
        }

        // Fall back to native text paste
        Editor.Paste();
    }

    /// <summary>Saves a clipboard bitmap as a PNG in the markdown folder; returns its filename.</summary>
    private async Task<string?> SavePastedBitmapAsync(Bitmap bitmap)
    {
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

        await using var stream = new FileStream(filePath, FileMode.Create);
#pragma warning disable CS0618
        bitmap.Save(stream);
#pragma warning restore CS0618
        return fileName;
    }

    #endregion
}
