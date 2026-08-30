using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Reemd.Services;

/// <summary>
/// Detects macOS pasteboard changes without fetching payload data on the UI thread.
/// </summary>
public sealed class MacClipboardChangeMonitor
{
    private const int ReadTimeoutMs = 1500;

    private nint? _last_processed_change_count;
    private nint? _pending_change_count;

    public bool HasChanged()
    {
        var current_change_count = ReadChangeCount();
        var has_changed = _last_processed_change_count == null ||
            _last_processed_change_count != current_change_count;
        if (has_changed)
            _pending_change_count = current_change_count;

        return has_changed;
    }

    public async Task<string> ReadTextAsync()
    {
        var start_info = new ProcessStartInfo
        {
            FileName = "/usr/bin/pbpaste",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start_info) ??
            throw new InvalidOperationException("Cannot start the macOS clipboard reader.");
        using var timeout_token_source = new CancellationTokenSource(ReadTimeoutMs);
        var output_task = process.StandardOutput.ReadToEndAsync(timeout_token_source.Token);

        try
        {
            await process.WaitForExitAsync(timeout_token_source.Token).ConfigureAwait(false);
            var clipboard_text = await output_task.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"macOS clipboard reader exited with code {process.ExitCode}.");

            _last_processed_change_count = _pending_change_count;
            _pending_change_count = null;
            return clipboard_text;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            throw new TimeoutException("macOS clipboard reader timed out.");
        }
    }

    public void AcknowledgeCurrent()
    {
        _last_processed_change_count = ReadChangeCount();
        _pending_change_count = null;
    }

    public void AcknowledgeRead()
    {
        _last_processed_change_count = _pending_change_count;
        _pending_change_count = null;
    }

    private static nint ReadChangeCount()
    {
        var pasteboard_class = objc_getClass("NSPasteboard");
        var general_pasteboard_selector = sel_registerName("generalPasteboard");
        var pasteboard = objc_msgSend_intptr(pasteboard_class, general_pasteboard_selector);
        var change_count_selector = sel_registerName("changeCount");
        return objc_msgSend_nint(pasteboard, change_count_selector);
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_intptr(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint(nint receiver, nint selector);
}
