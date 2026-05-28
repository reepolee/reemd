using System.Windows;
using System.Windows.Threading;

namespace Reemd;

/// <summary>
/// Partial class containing preview update logic: rendering, font sizes, and the preview debounce timer.
/// </summary>
public partial class MainWindow
{
    #region Preview

    private void UpdatePreview(string markdown, double? previewFontSize = null)
    {
        try
        {
            var size = previewFontSize ?? _previewFontSize;
            var html = _markdownConverter.ConvertToHtml(markdown, size, _isDarkMode, Config.VirtualBaseUrl);
            _isPreviewReady = false;

            if (Preview.CoreWebView2 != null)
            {
                Preview.NavigateToString(html);
                _pendingPreviewHtml = null;
            }
            else
            {
                // CoreWebView2 not yet initialized — store HTML to render once ready
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
            UpdatePreview(Editor.Text, _previewFontSize);
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
        UpdatePreview(Editor.Text, _previewFontSize);

        // Re-sync preview scroll to match editor after update
        Dispatcher.BeginInvoke(SyncEditorToPreview, DispatcherPriority.Background);
    }

    #endregion
}
