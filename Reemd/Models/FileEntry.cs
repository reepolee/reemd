namespace Reemd.Models;

/// <summary>
/// Represents a file entry in the file list, with pin state for sorting to top.
/// </summary>
public class FileEntry
{
    public string Name { get; set; } = "";
    public bool IsPinned { get; set; }
}
