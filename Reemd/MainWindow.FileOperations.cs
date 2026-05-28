using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reemd.Models;

namespace Reemd;

/// <summary>
/// Partial class containing file operations: loading, saving, creating, renaming files,
/// file list event handlers, and pin toggle logic.
/// </summary>
public partial class MainWindow
{
    #region File Loading & Saving

    private void LoadFile(string filePath)
    {
        if (_isClosing) return;

        // Save previous file first before switching
        if (_isDirty && _currentFilePath != null)
        {
            _ = SaveCurrentFileAsync();
        }

        if (!File.Exists(filePath))
        {
            SetStatus($"File not found: {Path.GetFileName(filePath)}");
            return;
        }

        // Read content — do NOT touch editor state on failure
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading {Path.GetFileName(filePath)}: {ex.Message}");
            return;
        }

        // Only now update state — everything succeeded
        _isLoadingDocument = true;
        try
        {
            _currentFilePath = filePath;
            _fileContentCache[filePath] = content;

            Editor.Text = content;
            UpdatePreview(content, _previewFontSize);

            UpdateTitle(filePath);
            SetStatus($"Editing: {Path.GetFileName(filePath)}");

            RestoreCursorPosition(filePath);

            _isDirty = false;
            UpdateSavedIndicator(true);

            // Restore per-file scroll position after layout is complete
            Dispatcher.BeginInvoke(RestorePerFileScroll, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            SetStatus($"Error rendering preview: {ex.Message}");
        }
        finally
        {
            _isLoadingDocument = false;
        }
    }

    private async Task SaveCurrentFileAsync()
    {
        if (_currentFilePath == null) return;

        try
        {
            SaveCursorPosition(_currentFilePath);

            var markdown = Editor.Text;

            await File.WriteAllTextAsync(_currentFilePath, markdown);
            _fileContentCache[_currentFilePath] = markdown;

            _isDirty = false;
            UpdateSavedIndicator(true);
            ScheduleGitHubSync();

            // Refresh file list to re-sort by last write time — suppress
            // SelectionChanged so we don't re-load the same file unnecessarily.
            _isLoadingDocument = true;
            RefreshFileList();
            _isLoadingDocument = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Save error: {ex.Message}");
            UpdateSavedIndicator(false);
        }
    }

    private async Task AutoSaveCurrentFileAsync()
    {
        if (_currentFilePath == null || !_isDirty || _isLoadingDocument) return;
        await SaveCurrentFileAsync();
    }

    private void UpdateTitle(string? filePath)
    {
        var fileName = filePath != null ? Path.GetFileName(filePath) : "Untitled";
        Title = $"Reemd - {fileName}";
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        _ = AutoSaveCurrentFileAsync();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingDocument) return;

        _isDirty = true;
        UpdateSavedIndicator(false);

        // Reset auto-save timer
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();

        // Reset preview timer (debounce — only renders after 400ms of no typing)
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateCursorPositionText();
    }

    #endregion

    #region File Creation & Navigation

    private void CreateNewFile()
    {
        if (string.IsNullOrWhiteSpace(_markdownFolder)) return;
        try
        {
            // Find a unique filename
            int counter = 1;
            string fileName;
            do
            {
                fileName = $"Untitled-{counter}.md";
                counter++;
            } while (File.Exists(Path.Combine(_markdownFolder, fileName)));

            var fullPath = Path.Combine(_markdownFolder, fileName);
            File.WriteAllText(fullPath, "");

            RefreshFileList();
            var createdMatch = _fileList.FirstOrDefault(f => f.Name == fileName);
            if (createdMatch != null)
                FileListBox.SelectedItem = createdMatch;
            Editor.Focus();
            SetStatus($"Created {fileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Error creating file: {ex.Message}");
        }
    }

    private void SelectNextFile()
    {
        if (_fileList.Count == 0) return;
        if (FileListBox.SelectedIndex < _fileList.Count - 1)
            FileListBox.SelectedIndex++;
        else
            FileListBox.SelectedIndex = 0; // wrap to first
    }

    private void SelectPreviousFile()
    {
        if (_fileList.Count == 0) return;
        if (FileListBox.SelectedIndex > 0)
            FileListBox.SelectedIndex--;
        else
            FileListBox.SelectedIndex = _fileList.Count - 1; // wrap to last
    }

    #endregion

    #region File List Event Handlers

    private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDocument) return;

        var selectedEntry = FileListBox.SelectedItem as FileEntry;
        if (selectedEntry == null) return;

        _ = AutoSaveCurrentFileAsync();

        var fullPath = Path.Combine(_markdownFolder, selectedEntry.Name);
        LoadFile(fullPath);
    }

    /// <summary>
    /// Toggles the pin state of a file. Pinned files appear at the top of the list.
    /// </summary>
    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var entry = button?.DataContext as FileEntry;
        if (entry == null) return;

        // Toggle pin state
        entry.IsPinned = !entry.IsPinned;

        if (entry.IsPinned)
            _pinnedFilenames.Add(entry.Name);
        else
            _pinnedFilenames.Remove(entry.Name);

        SavePinnedFilenames();

        // Re-sort the list so pinned files appear at the top
        var currentSelection = (FileListBox.SelectedItem as FileEntry)?.Name;

        var sorted = _fileList
            .OrderByDescending(f => f.IsPinned ? 1 : 0)
            .ThenByDescending(f =>
            {
                var fullPath = Path.Combine(_markdownFolder, f.Name);
                try { return File.GetLastWriteTime(fullPath); }
                catch { return DateTime.MinValue; }
            })
            .ToList();

        _fileList.Clear();
        foreach (var item in sorted)
            _fileList.Add(item);

        // Re-select
        if (currentSelection != null)
        {
            var match = _fileList.FirstOrDefault(f => f.Name == currentSelection);
            if (match != null)
                FileListBox.SelectedItem = match;
        }

        SetStatus(entry.IsPinned
            ? $"Pinned: {entry.Name}"
            : $"Unpinned: {entry.Name}");
    }

    private void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var selectedEntry = FileListBox.SelectedItem as FileEntry;
        if (selectedEntry == null) return;

        var fullPath = Path.Combine(_markdownFolder, selectedEntry.Name);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void FileListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F2 — rename selected file
        if (e.Key == Key.F2)
        {
            var selectedEntry = FileListBox.SelectedItem as FileEntry;
            if (selectedEntry == null) return;

            RenameFile(selectedEntry.Name);
            e.Handled = true;
        }
    }

    #endregion

    #region Rename File

    /// <summary>
    /// Shows a rename dialog for the given filename (without path).
    /// Renames the file on disk and updates all internal state.
    /// </summary>
    private void RenameFile(string fileName)
    {
        var oldPath = Path.Combine(_markdownFolder, fileName);
        if (!File.Exists(oldPath)) return;

        var oldNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        // Show rename dialog
        var bg = _isDarkMode ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Color.FromRgb(0xF0, 0xF0, 0xF0);
        var fg = _isDarkMode ? Colors.White : Colors.Black;

        var dialog = new Window
        {
            Title = "Rename File",
            Width = 400,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg)
        };

        var stack = new StackPanel { Margin = new Thickness(12) };

        stack.Children.Add(new TextBlock
        {
            Text = "New name:",
            Foreground = new SolidColorBrush(fg),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var textBox = new TextBox
        {
            Text = oldNameWithoutExt,
            Padding = new Thickness(6, 3, 6, 3)
        };
        textBox.Focus();
        textBox.SelectAll();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var okBtn = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 70,
            Height = 24,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Width = 70,
            Height = 24
        };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);

        stack.Children.Add(textBox);
        stack.Children.Add(buttonPanel);
        dialog.Content = stack;

        dialog.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        okBtn.Click += (_, _) => dialog.DialogResult = true;
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;

        var result = dialog.ShowDialog();
        if (result != true) return;

        var newName = textBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        // Ensure .md extension
        if (!newName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            newName += ".md";

        var newPath = Path.Combine(_markdownFolder, newName);

        // Check if the name actually changed (on case-insensitive filesystems like Windows NTFS,
        // a case-only change like "file.md" → "FILE.md" requires special handling — see below)
        bool onlyCaseChanged = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
        if (onlyCaseChanged && string.Equals(newPath, oldPath, StringComparison.Ordinal))
            return;

        // Check if target already exists
        if (File.Exists(newPath))
        {
            SetStatus($"Cannot rename: '{newName}' already exists");
            return;
        }

        // Validate filename
        var invalidChars = Path.GetInvalidFileNameChars();
        if (newName.Any(c => invalidChars.Contains(c)))
        {
            SetStatus($"Invalid characters in filename");
            return;
        }

        try
        {
            if (!onlyCaseChanged)
            {
                File.Move(oldPath, newPath);
            }
            else
            {
                // Case-only rename on case-insensitive filesystem:
                // Move to a temp name first, then to the desired case.
                var tempPath = oldPath + ".tmp_rename";
                File.Move(oldPath, tempPath);
                File.Move(tempPath, newPath);
            }

            // Update internal state to use the new path
            if (_cursorPositions.TryGetValue(oldPath, out var cursorPos))
            {
                _cursorPositions[newPath] = cursorPos;
                _cursorPositions.Remove(oldPath);
            }

            if (_scrollRatios.TryGetValue(oldPath, out var scrollRatio))
            {
                _scrollRatios[newPath] = scrollRatio;
                _scrollRatios.Remove(oldPath);
            }

            if (_fileContentCache.TryGetValue(oldPath, out var content))
            {
                _fileContentCache[newPath] = content;
                _fileContentCache.Remove(oldPath);
            }

            // If this was the current file, update the current path
            if (string.Equals(_currentFilePath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentFilePath = newPath;
                Editor.Text = content ?? File.ReadAllText(newPath);
                UpdateTitle(newPath);
            }

            // Update last write time so the file jumps to the top of the sorted list
            File.SetLastWriteTime(newPath, DateTime.Now);

            // Refresh the file list and select the renamed file
            RefreshFileList();
            var newFileName = Path.GetFileName(newPath);
            var match = _fileList.FirstOrDefault(f => f.Name == newFileName);
            if (match != null)
            {
                FileListBox.SelectedItem = match;
            }

            SetStatus($"Renamed to {newName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Rename failed: {ex.Message}");
        }
    }

    #endregion
}
