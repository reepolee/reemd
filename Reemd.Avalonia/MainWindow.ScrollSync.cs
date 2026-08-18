using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Reemd;

public partial class MainWindow
{
    private bool _previewAdapterReady;

    #region Scroll Sync

    private void OnEditorLoaded(object? sender, RoutedEventArgs e)
    {
        _editorScrollViewer = FindDescendant<ScrollViewer>(Editor);
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollChanged += OnEditorScrollChanged;
            RestoreEditorScroll();
        }
    }

    private void OnPreviewAdapterCreated()
    {
        _previewAdapterReady = true;

        // If there's pending HTML from before initialization, render it now
        if (_pendingPreviewHtml != null)
        {
            _isPreviewReady = false;
            Preview.NavigateToString(_pendingPreviewHtml);
            _pendingPreviewHtml = null;
        }

        // Re-apply preview font size now that the adapter is ready.
        ApplyPreviewFontSize();
    }

    private void Preview_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;

        _isPreviewReady = true;

        // Inject scroll sync script into the page
        _ = Preview.InvokeScript(
            "(function(){ window.__reemdScrollRatio=0; window.addEventListener('scroll',function(){var sh=document.documentElement.scrollHeight-document.documentElement.clientHeight;var r=sh>0?document.documentElement.scrollTop/sh:0;window.__reemdScrollRatio=r;try{invokeCSharpAction(JSON.stringify({type:'scroll',ratio:r}))}catch(e){}}); })();");

        // Restore scroll position for this file after navigation
        if (_currentFilePath != null && _scrollRatios.TryGetValue(_currentFilePath, out var ratio) && ratio > 0)
        {
            _ = Preview.InvokeScript(
                "document.documentElement.scrollTop=" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*(document.documentElement.scrollHeight-document.documentElement.clientHeight)");
        }
    }

    private void OnEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll) return;

        if (_currentFilePath != null && _editorScrollViewer != null)
        {
            _scrollRatios[_currentFilePath] = ScrollableHeight(_editorScrollViewer) > 0
                ? _editorScrollViewer.Offset.Y / ScrollableHeight(_editorScrollViewer)
                : 0;
        }

        if (_isPreviewReady)
        {
            _ = SyncEditorScrollToPreviewAsync();
        }
    }

    private static double ScrollableHeight(ScrollViewer viewer) =>
        Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);

    private async Task SyncEditorScrollToPreviewAsync()
    {
        if (_editorScrollViewer == null) return;

        var ratio = ScrollableHeight(_editorScrollViewer) > 0
            ? _editorScrollViewer.Offset.Y / ScrollableHeight(_editorScrollViewer)
            : 0;

        try
        {
            await Preview.InvokeScript(
                "document.documentElement.scrollTop=" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*(document.documentElement.scrollHeight-document.documentElement.clientHeight)");
        }
        catch
        {
        }
    }

    private void OnPreviewWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_isSyncingScroll || _editorScrollViewer == null || _currentFilePath == null) return;

        try
        {
            if (string.IsNullOrEmpty(e.Body)) return;
            var msg = JsonSerializer.Deserialize<ScrollMessage>(e.Body);
            if (msg?.type != "scroll") return;

            _isSyncingScroll = true;
            var ratio = Math.Clamp(msg.ratio, 0.0, 1.0);
            _editorScrollViewer.Offset = new Vector(_editorScrollViewer.Offset.X, ScrollableHeight(_editorScrollViewer) * ratio);
            _scrollRatios[_currentFilePath] = ratio;
        }
        catch
        {
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private class ScrollMessage
    {
        public string type { get; set; } = "";
        public double ratio { get; set; }
    }

    private void RestoreEditorScroll()
    {
        if (_editorScrollViewer == null || _currentFilePath == null) return;
        if (!_scrollRatios.TryGetValue(_currentFilePath, out var ratio) || ratio <= 0) return;
        if (ScrollableHeight(_editorScrollViewer) <= 0) return;

        _isSyncingScroll = true;
        try
        {
            _editorScrollViewer.Offset = new Vector(_editorScrollViewer.Offset.X, ScrollableHeight(_editorScrollViewer) * ratio);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void RestorePerFileScroll()
    {
        RestoreEditorScroll();
        _ = SyncEditorScrollToPreviewAsync();
    }

    private void SyncEditorToPreview()
    {
        if (_isPreviewReady)
        {
            _ = SyncEditorScrollToPreviewAsync();
        }
    }

    /// <summary>
    /// Recursively searches the visual tree for a descendant of type T.
    /// </summary>
    private static T? FindDescendant<T>(Visual parent) where T : class
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is T match)
                return match;

            if (child is Visual visualChild)
            {
                var result = FindDescendant<T>(visualChild);
                if (result != null)
                    return result;
            }
        }

        return null;
    }

    #endregion
}
