using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Reemd;

public partial class MainWindow
{
    private bool _previewAdapterReady;
    private double _lastAppliedPreviewRatio = double.NaN;
    private int _editorPageToken;

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

        if (_editorScrollViewer != null)
        {
            var ratio = ScrollableHeight(_editorScrollViewer) > 0
                ? _editorScrollViewer.Offset.Y / ScrollableHeight(_editorScrollViewer)
                : 0;

            // ScrollChanged is raised on LayoutUpdated, i.e. after _isSyncingScroll
            // has already been reset, so a preview-initiated editor scroll would
            // otherwise bounce straight back into the preview. Compare against the
            // ratio the preview just applied and swallow that single echo.
            if (!double.IsNaN(_lastAppliedPreviewRatio) &&
                Math.Abs(ratio - _lastAppliedPreviewRatio) < 0.0005)
            {
                _lastAppliedPreviewRatio = double.NaN;
                return;
            }

            if (_currentFilePath != null)
                _scrollRatios[_currentFilePath] = ratio;
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
            _lastAppliedPreviewRatio = ratio;
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
    /// Scrolls the editor by one viewport height (+1 = down, -1 = up) with a short
    /// ease-out animation, keeping the preview in sync. Used for the PageDown/PageUp keys.
    /// </summary>
    private async void PageEditor(int direction)
    {
        if (_editorScrollViewer == null) return;

        var scrollable = ScrollableHeight(_editorScrollViewer);
        if (scrollable <= 0) return;

        var page = _editorScrollViewer.Viewport.Height;
        if (page <= 0) return;

        var target = direction > 0
            ? Math.Min(_editorScrollViewer.Offset.Y + page, scrollable)
            : Math.Max(_editorScrollViewer.Offset.Y - page, 0);

        await AnimateEditorScroll(target);
    }

    /// <summary>
    /// Animates the editor's ScrollViewer to <paramref name="target"/> with an ease-out
    /// animation, keeping the preview in sync. Cancels any in-flight animation. Shared by
    /// page-up/down and scroll-to-top/bottom.
    /// </summary>
    private async Task AnimateEditorScroll(double target)
    {
        if (_editorScrollViewer == null) return;

        var scrollable = ScrollableHeight(_editorScrollViewer);
        if (scrollable <= 0) return;

        target = Math.Clamp(target, 0, scrollable);

        var start = _editorScrollViewer.Offset.Y;
        if (Math.Abs(target - start) < 0.5) return;

        // Invalidate any in-flight animation (e.g. a held key auto-repeating) so
        // overlapping animations don't fight over the offset.
        var token = ++_editorPageToken;

        // Suppress scroll-sync feedback while animating so the preview's deferred
        // scroll events can't echo back and re-set the editor mid-animation.
        _isSyncingScroll = true;
        try
        {
            const int durationMs = 200;
            var watch = Stopwatch.StartNew();
            while (token == _editorPageToken)
            {
                var t = Math.Min(1.0, watch.Elapsed.TotalMilliseconds / durationMs);
                var eased = 1.0 - Math.Pow(1.0 - t, 3.0); // ease-out cubic
                _editorScrollViewer.Offset = new Vector(_editorScrollViewer.Offset.X, start + (target - start) * eased);

                if (_isPreviewReady)
                    _ = SyncEditorScrollToPreviewAsync();

                if (t >= 1.0) break;
                await Task.Delay(16);
            }
        }
        finally
        {
            // Only release the guard if this animation is still the latest one.
            if (token == _editorPageToken)
                _isSyncingScroll = false;
        }

        if (token != _editorPageToken) return;

        if (_currentFilePath != null && scrollable > 0)
            _scrollRatios[_currentFilePath] = target / scrollable;

        // Final sync so the preview lands exactly on the target position (also covers
        // the case where the offset change didn't alter the scroll ratio).
        Dispatcher.UIThread.Post(SyncEditorToPreview, DispatcherPriority.Background);
    }

    /// <summary>
    /// Scrolls the preview by one viewport height (+1 = down, -1 = up). The preview's
    /// injected scroll listener keeps the editor in sync.
    /// </summary>
    private async void PagePreview(int direction)
    {
        if (!_isPreviewReady) return;

        try
        {
            // Animate the page jump; fall back to an instant jump on engines that
            // don't support scrollTo options.
            var sign = direction > 0 ? "+" : "-";
            var script =
                "(function(){" +
                "var t=document.documentElement.scrollTop" + sign + "window.innerHeight*0.9;" +
                "try{document.documentElement.scrollTo({top:t,behavior:'smooth'});}" +
                "catch(e){document.documentElement.scrollTop=t;}" +
                "})();";
            await Preview.InvokeScript(script);
        }
        catch
        {
            // Preview paging is best-effort.
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
