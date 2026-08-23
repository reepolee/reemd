# Auto-update release parsing

## Symptom

The Update button reported that the latest GitHub release had an invalid version.

## Cause

GitHub Release API fields use snake case. `tag_name` was not mapped to the updater's `TagName` property, leaving it empty before version parsing.

## Fix

Explicit JSON property mappings now read both `tag_name` and `browser_download_url`.
