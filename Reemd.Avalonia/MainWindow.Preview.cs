using Avalonia.Threading;

namespace Reemd;

/// <summary>
/// Partial class containing preview update logic: rendering, font sizes, and the preview debounce timer.
/// </summary>
public partial class MainWindow
{
    #region Preview

    private string? _lastPreviewHead;

    private void UpdatePreview(string markdown, double? previewFontSize = null, bool inPlace = false)
    {
        try
        {
            var size = previewFontSize ?? _previewFontSize;
            var html = _markdownConverter.ConvertToHtml(markdown, size, _isDarkMode, _markdownFolder);

            // Split into <head> (styles/scripts/theme) and body content. When only the
            // body changed (normal typing), swap it in place: no page reload, so the
            // preview keeps its scroll position and doesn't flicker.
            var bodyStart = html.IndexOf("<body>", StringComparison.Ordinal);
            var bodyEnd = html.LastIndexOf("</body>", StringComparison.Ordinal);
            string? head = bodyStart >= 0 && bodyEnd > bodyStart ? html[..bodyStart] : null;

            if (inPlace && head != null && head == _lastPreviewHead && _isPreviewReady && _previewAdapterReady)
            {
                var body = html.Substring(bodyStart + 6, bodyEnd - bodyStart - 6);
                var literal = System.Text.Json.JsonSerializer.Serialize(body);
                _restorePreviewPixels = false;
                _ = Preview.InvokeScript("document.body.innerHTML=" + literal + ";if(window.hljs)hljs.highlightAll();");
                return;
            }

            _lastPreviewHead = head;
            _isPreviewReady = false;

            if (_previewAdapterReady)
            {
                Preview.NavigateToString(html);
                _pendingPreviewHtml = null;
            }
            else
            {
                // Adapter not yet initialized — store HTML to render once ready
                _pendingPreviewHtml = html;
            }
        }
        catch
        {
            // Preview is best-effort — never crash the editor on render failures
        }
    }

    private void ApplyEditorFontSize()
    {
        Editor.FontSize = _editorFontSize;
        ShowCombinedFontSizes();
        SaveSettings();
    }

    private void ApplyPreviewFontSize()
    {
        ShowCombinedFontSizes();
        if (!string.IsNullOrEmpty(Editor.Text))
        {
            UpdatePreview(Editor.Text ?? string.Empty, _previewFontSize);
        }
    }

    private void ShowCombinedFontSizes()
    {
        FontSizeText.Text = $"Editor: {_editorFontSize}px";
        PreviewFontSizeText.Text = $"Preview: {_previewFontSize}px";
    }

    /// <summary>
    /// Fires after a 400ms pause in typing to refresh the preview.
    /// </summary>
    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _restorePreviewPixels = true;
        UpdatePreview(Editor.Text ?? string.Empty, _previewFontSize, inPlace: true);

        // Re-sync preview scroll to match editor after update
        Dispatcher.UIThread.Post(SyncEditorToPreview, DispatcherPriority.Background);
    }

    #endregion
}
