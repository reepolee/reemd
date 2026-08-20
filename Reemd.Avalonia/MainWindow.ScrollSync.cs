using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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

        // Guarantee the restored caret is visible after a file switch — the saved
        // scroll ratio and caret position can be out of sync (e.g. the caret moved
        // after the ratio was saved), and the caret is never revealed horizontally.
        EnsureCaretVisible();

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
    /// The caret is moved along with the page so it stays visible and a pure-keyboard
    /// user can keep typing immediately.
    /// </summary>
    private async void PageEditor(int direction, bool extendSelection = false)
    {
        if (_editorScrollViewer == null) return;

        var scrollable = ScrollableHeight(_editorScrollViewer);
        if (scrollable <= 0) return;

        var page = _editorScrollViewer.Viewport.Height;
        if (page <= 0) return;

        // Move the caret by the same distance as the scroll, keeping its column,
        // so it ends up at the same spot on screen one page deeper into the text.
        MoveCaretByPage(direction, page, extendSelection);

        var target = direction > 0
            ? Math.Min(_editorScrollViewer.Offset.Y + page, scrollable)
            : Math.Max(_editorScrollViewer.Offset.Y - page, 0);

        await AnimateEditorScroll(target);

        // The page animation only scrolls vertically; the caret's column can fall
        // outside the viewport horizontally (e.g. paging down to a line shorter
        // than the one we left, on a long unwrapped line). Reveal it after the
        // animation so the vertical page motion stays smooth.
        EnsureCaretVisible();
    }

    /// <summary>
    /// Moves the editor caret one viewport height up or down the document (keeping the
    /// column), so it follows a PageUp/PageDown scroll and always stays visible. With
    /// <paramref name="extendSelection"/> (Shift+PageUp/PageDown) the selection anchor
    /// stays put and the caret end moves, extending the selection by a page.
    /// </summary>
    private void MoveCaretByPage(int direction, double page, bool extendSelection = false)
    {
        var presenter = FindDescendant<TextPresenter>(Editor);
        var layout = presenter?.TextLayout;
        if (layout == null || layout.TextLines.Count == 0) return;

        var caretIndex = Editor.CaretIndex;
        var caretRect = GetCaretDocumentRect(layout, caretIndex);

        // Target document Y one page away, clamped to the text extent.
        var targetY = Math.Clamp(caretRect.Y + direction * page, 0, layout.Height);

        // The visual line at that Y.
        var targetLine = layout.TextLines[^1];
        double y = 0;
        foreach (var line in layout.TextLines)
        {
            targetLine = line;
            if (y + line.Height > targetY) break;
            y += line.Height;
        }

        // Place the caret in the target line at the same column (collapses any selection).
        var hit = targetLine.GetCharacterHitFromDistance(caretRect.X);
        var newIndex = Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength,
            targetLine.FirstTextSourceIndex,
            targetLine.FirstTextSourceIndex + targetLine.Length);

        if (newIndex == caretIndex) return;

        if (extendSelection)
        {
            // The end of the selection opposite the caret stays fixed while paging.
            // Compute it before changing the caret: Avalonia collapses the selection
            // whenever CaretIndex changes, so the range is re-established afterwards.
            var anchor = caretIndex == Editor.SelectionEnd ? Editor.SelectionStart : Editor.SelectionEnd;
            Editor.CaretIndex = newIndex;
            Editor.SelectionStart = Math.Min(anchor, newIndex);
            Editor.SelectionEnd = Math.Max(anchor, newIndex);
        }
        else
        {
            Editor.CaretIndex = newIndex;
            Editor.SelectionStart = newIndex;
            Editor.SelectionEnd = newIndex;
        }
    }

    /// <summary>
    /// Returns the document-space rect of the caret (X = column offset within its
    /// line, Y = top of the visual line, Height = line height). The caller must
    /// pass a non-empty layout.
    /// </summary>
    private static Rect GetCaretDocumentRect(TextLayout layout, int caretIndex)
    {
        // The visual line holding the caret (a caret at the very end of the text
        // belongs to the last line), accumulating the Y of its top edge.
        double caretY = 0;
        var caretLine = layout.TextLines[^1];
        foreach (var line in layout.TextLines)
        {
            var lineEnd = line.FirstTextSourceIndex + line.Length + line.NewLineLength;
            if (caretIndex < lineEnd)
            {
                caretLine = line;
                break;
            }
            caretY += line.Height;
        }

        // Caret column within its line (distance from the leading edge).
        var xIndex = Math.Clamp(caretIndex,
            caretLine.FirstTextSourceIndex,
            caretLine.FirstTextSourceIndex + caretLine.Length);
        var caretX = caretLine.GetDistanceFromCharacterHit(new CharacterHit(xIndex, 0));

        return new Rect(caretX, caretY, 0, caretLine.Height);
    }

    /// <summary>
    /// Adjusts the editor's scroll offset (both axes) so the caret is inside the
    /// viewport. Safety net for scroll-to-top/bottom: Avalonia only brings the caret
    /// into view when its index actually changes, so a caret that's already at the
    /// top/bottom can stay hidden (e.g. off-screen horizontally on a long unwrapped
    /// line) after Ctrl+Home/Ctrl+End.
    /// </summary>
    private void EnsureCaretVisible()
    {
        if (_editorScrollViewer == null) return;

        var presenter = FindDescendant<TextPresenter>(Editor);
        var layout = presenter?.TextLayout;
        if (layout == null || layout.TextLines.Count == 0) return;

        var caretRect = GetCaretDocumentRect(layout, Editor.CaretIndex);

        var viewport = _editorScrollViewer.Viewport;
        var offset = _editorScrollViewer.Offset;
        var scrollableX = Math.Max(0, _editorScrollViewer.Extent.Width - viewport.Width);
        var scrollableY = Math.Max(0, _editorScrollViewer.Extent.Height - viewport.Height);

        var x = offset.X;
        if (caretRect.Left < offset.X)
            x = caretRect.Left;
        else if (caretRect.Right > offset.X + viewport.Width)
            x = caretRect.Right - viewport.Width;

        var y = offset.Y;
        if (caretRect.Top < offset.Y)
            y = caretRect.Top;
        else if (caretRect.Bottom > offset.Y + viewport.Height)
            y = caretRect.Bottom - viewport.Height;

        x = Math.Clamp(x, 0, scrollableX);
        y = Math.Clamp(y, 0, scrollableY);

        if (Math.Abs(x - offset.X) > 0.5 || Math.Abs(y - offset.Y) > 0.5)
            _editorScrollViewer.Offset = new Vector(x, y);
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
