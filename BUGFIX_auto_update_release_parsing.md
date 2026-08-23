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
