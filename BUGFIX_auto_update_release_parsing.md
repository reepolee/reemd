# Auto-update release parsing

## Symptom

The Update button reported that the latest GitHub release had an invalid version.

## Cause

GitHub Release API fields use snake case. `tag_name` was not mapped to the updater's `TagName` property, leaving it empty before version parsing.

## Fix

Explicit JSON property mappings now read both `tag_name` and `browser_download_url`.

## GitHub CLI authentication status

### Symptom

ReeMD reported that GitHub CLI was not authenticated while Git operations worked in VS Code.

### Cause

`gh auth status` writes its signed-in status to standard error. ReeMD only inspected standard output.

### Fix

Authentication validation now combines both streams before checking the GitHub CLI status and extracting the active account name.

## GitHub CLI path in macOS apps

### Symptom

ReeMD reported that GitHub CLI was not authenticated even though `gh auth status` succeeded in Terminal.

### Cause

macOS applications launched from Finder do not inherit Homebrew's PATH. ReeMD could not find the installed `gh` executable.

### Fix

ReeMD now resolves GitHub CLI from Homebrew's Apple Silicon and Intel installation locations before running its authentication check.

## Update archive lock

### Symptom

The updater could not extract its temporary ZIP because the file was in use.

### Cause

The archive output stream remained open until the method returned, but extraction started before that point.

### Fix

The download streams now close before ReeMD extracts the staged archive.

## Narrow toolbar layout

### Symptom

Toolbar controls overlapped at narrower window widths.

### Cause

The left and right toolbar groups shared one fixed row without overflow handling.

### Fix

The action controls wrap within the toolbar's left column, while project shortcuts remain right-aligned in their original column.
