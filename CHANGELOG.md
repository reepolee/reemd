# Changelog

## [Unreleased]

### Added
- **Force git pull** — manual pull button (⬇️) in toolbar and `Ctrl+Shift+P` hotkey to pull latest changes from remote and reload editor if file changed on disk
- **Auto-sync pull** — sync timer now pulls before pushing, keeping local and remote in sync across devices
- Graceful reload on pull — only reloads editor file from disk if you have no unsaved changes (`_isDirty` check prevents clobbering active edits)

### Fixed
- Sync now pulls first before pushing, avoiding conflicts when working on multiple devices
