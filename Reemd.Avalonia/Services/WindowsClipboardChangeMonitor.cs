using System.Runtime.InteropServices;

namespace Reemd.Services;

/// <summary>
/// Reads the Windows clipboard sequence number without fetching clipboard data.
/// </summary>
public sealed class WindowsClipboardChangeMonitor
{
    private uint? _last_processed_sequence_number;
    private uint? _pending_sequence_number;

    public bool HasChanged()
    {
        var current_sequence_number = GetClipboardSequenceNumber();
        var has_changed = _last_processed_sequence_number == null ||
            _last_processed_sequence_number != current_sequence_number;
        if (has_changed)
            _pending_sequence_number = current_sequence_number;

        return has_changed;
    }

    public void AcknowledgeCurrent()
    {
        _last_processed_sequence_number = GetClipboardSequenceNumber();
        _pending_sequence_number = null;
    }

    public void AcknowledgeRead()
    {
        _last_processed_sequence_number = _pending_sequence_number;
        _pending_sequence_number = null;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
