namespace Reemd.Models;

/// <summary>
/// Stores the cursor position for a file so we can restore it when switching back.
/// </summary>
public record CursorPosition(int CaretIndex, int SelectionStart, int SelectionLength);
