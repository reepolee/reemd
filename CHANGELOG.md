# Changelog

## [Unreleased]

### Added
- **Per-device project shortcuts** — shortcut settings are saved to a `<device>.<os>.reemd.projects.json` file (e.g. `comet.win.reemd.projects.json`) at the repo root, so each machine keeps its own paths/commands and opening another repo automatically loads your device's shortcuts (and hotkey combo)
- **Platform default command** — new project shortcuts pre-fill a platform-appropriate launch command (`code {path} && wt -d {path}` on Windows, `code {path} && open -a iTerm {path}` on macOS)
- **Force git pull** — manual pull button (⬇️) in toolbar and `Ctrl+Shift+P` hotkey to pull latest changes from remote and reload editor if file changed on disk
- **Auto-sync pull** — sync timer now pulls before pushing, keeping local and remote in sync across devices
- Graceful reload on pull — only reloads editor file from disk if you have no unsaved changes (`_isDirty` check prevents clobbering active edits)

### Fixed
- Sync now pulls first before pushing, avoiding conflicts when working on multiple devices
- PageUp/PageDown now move the editor caret along with the scroll, so the cursor stays visible and you can keep typing immediately (Shift+PageUp/PageDown or Cmd+Shift+Up/Down on MacBook keyboards extends the selection; the caret is also revealed horizontally after paging on long unwrapped lines)
- Ctrl+Home/Ctrl+End (and the toolbar scroll buttons) now keep the caret visible after scrolling to the top/bottom of the document, including horizontal reveal on long unwrapped lines
- Project Shortcuts dialog now uses the theme's system background and text colors, fixing low contrast in light and dark mode
- The editor caret is now guaranteed to be scrolled into view (both axes) after switching files and when jumping between find/replace matches
- Shift+Ctrl+Home/Ctrl+End extend the selection to the start/end of the document, keeping the caret visible
- Cmd+Home/Cmd+End on macOS scroll to the document top/bottom with the caret kept visible, and Cmd+Shift+Home/Cmd+Shift+End extend the selection to the document start/end (the Mac equivalents of Ctrl+Home/End and Shift+Ctrl+Home/End)
