using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Reemd;

public partial class MainWindow
{
    #region Scroll Sync

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        _editorScrollViewer = FindVisualChild<ScrollViewer>(Editor);
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ScrollChanged += OnEditorScrollChanged;
            RestoreEditorScroll();
        }
    }

    private void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        // Ensure CoreWebView2 is initialized — this is required before NavigateToString will work.
        // Without this call, CoreWebView2InitializationCompleted may never fire.
        _ = Preview.EnsureCoreWebView2Async();
    }

    private void Preview_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            // Disable WebView2's built-in browser zoom (Ctrl+Scroll/Plus/Minus) so it
            // doesn't intercept our Ctrl+Shift+Scroll/Plus/Minus preview font size control.
            Preview.CoreWebView2.Settings.IsZoomControlEnabled = false;

            Preview.CoreWebView2.WebMessageReceived += OnPreviewWebMessageReceived;
            UpdateVirtualHostMapping();

            // If there's pending HTML from before initialization, render it now
            if (_pendingPreviewHtml != null)
            {
                _isPreviewReady = false;
                Preview.NavigateToString(_pendingPreviewHtml);
                _pendingPreviewHtml = null;
            }

            // Re-apply preview font size now that WebView2 is ready,
            // ensuring the correct saved font size is always displayed.
            ApplyPreviewFontSize();
        }
    }

    private void Preview_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isPreviewReady = true;

        // Inject scroll sync script into the page
        _ = Preview.ExecuteScriptAsync(
            "(function(){ window.__reemdScrollRatio=0; window.addEventListener('scroll',function(){var sh=document.documentElement.scrollHeight-document.documentElement.clientHeight;var r=sh>0?document.documentElement.scrollTop/sh:0;window.__reemdScrollRatio=r;try{window.chrome.webview.postMessage(JSON.stringify({type:'scroll',ratio:r}))}catch(e){}}); })();");

        // Restore scroll position for this file after navigation
        if (_currentFilePath != null && _scrollRatios.TryGetValue(_currentFilePath, out var ratio) && ratio > 0)
        {
            _ = Preview.ExecuteScriptAsync(
                "document.documentElement.scrollTop=" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*(document.documentElement.scrollHeight-document.documentElement.clientHeight)");
        }
    }

    private void OnEditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll) return;

        if (_currentFilePath != null)
        {
            _scrollRatios[_currentFilePath] = _editorScrollViewer!.ScrollableHeight > 0
                ? _editorScrollViewer.VerticalOffset / _editorScrollViewer.ScrollableHeight
                : 0;
        }

        if (_isPreviewReady)
        {
            _ = SyncEditorScrollToPreviewAsync();
        }
    }

    private async Task SyncEditorScrollToPreviewAsync()
    {
        if (_editorScrollViewer == null) return;

        var ratio = _editorScrollViewer.ScrollableHeight > 0
            ? _editorScrollViewer.VerticalOffset / _editorScrollViewer.ScrollableHeight
            : 0;

        try
        {
            await Preview.ExecuteScriptAsync(
                "document.documentElement.scrollTop=" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*(document.documentElement.scrollHeight-document.documentElement.clientHeight)");
        }
        catch
        {
        }
    }

    private void OnPreviewWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_isSyncingScroll || _editorScrollViewer == null || _currentFilePath == null) return;

        try
        {
            var msg = JsonSerializer.Deserialize<ScrollMessage>(e.TryGetWebMessageAsString());
            if (msg?.type != "scroll") return;

            _isSyncingScroll = true;
            var ratio = Math.Clamp(msg.ratio, 0.0, 1.0);
            _editorScrollViewer.ScrollToVerticalOffset(_editorScrollViewer.ScrollableHeight * ratio);
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
        if (_editorScrollViewer.ScrollableHeight <= 0) return;

        _isSyncingScroll = true;
        try
        {
            _editorScrollViewer.ScrollToVerticalOffset(
                _editorScrollViewer.ScrollableHeight * ratio);
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
    /// Recursively searches the visual tree for a child of type T.
    /// </summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
                return t;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }

        return null;
    }

    #endregion
}
