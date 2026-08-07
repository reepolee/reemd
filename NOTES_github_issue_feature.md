# Feature: Create GitHub Issue via keyboard shortcut

**Status: implemented (2026-08-07).** Ctrl+Shift+G opens `NewIssueDialog`
([Dialogs/NewIssueDialog.xaml](Reemd/Dialogs/NewIssueDialog.xaml)) with repo combo (+Reload
button), title, and plain-text description. Repo list and issue creation go through new
`GitHubService.ListReeRepositoriesAsync` / `CreateIssueAsync`
([Services/GitHubService.cs](Reemd/Services/GitHubService.cs)). Image/link handling was
explicitly deferred (v1 is plain text) - see decisions below if picking that up later.

Original analysis pass (kept for context) follows.

## Goal (as requested)

- Global keyboard shortcut opens a dialog.
- Dialog has: repo select (reepolee org, repos matching `ree*`), title, description.
- Description reuses the same image/link handling as the main markdown editor.
- Repo list should be **cached** (not re-fetched on every dialog open) with a
  manual **reload button** to refresh it.
- Submits to create a new GitHub Issue on the selected repo.

## Existing building blocks found in this repo

### GitHub access: `gh` CLI via subprocess, not a REST client
- [Services/GitHubService.cs](Reemd/Services/GitHubService.cs) wraps the `gh` and `git`
  executables via `Process` (see `RunProcessAsync`, line ~164). No Octokit/REST client
  dependency exists.
- `CheckAuthAsync()` (line 22) runs `gh auth status` and parses the logged-in user from
  stdout; also runs `gh repo view --json nameWithOwner -q .nameWithOwner` to get the
  current repo.
- There is **no existing "list repos" or "create issue" call**. Both would be new methods
  on `GitHubService`, following the same `RunGhCommandAsync(...)` pattern, e.g.:
  - List repos: `gh repo list reepolee --json name,nameWithOwner -q '...' --limit 100`
    (filter client-side or via `--json` + jq-style `-q` for `ree*` prefix).
  - Create issue: `gh issue create --repo <owner/name> --title "..." --body "..."` (or
    `--body-file -` piping stdin to avoid shell-escaping a multi-line/markdown body).
- Auth/user/repo state is cached on the service instance (`IsAuthenticated`, `CurrentUser`,
  `CurrentRepo` - simple in-memory fields, no persistence). A new `_cachedReeRepos` field
  following this same pattern would satisfy the "cache with reload button" ask.
- Timeout pattern: `RunProcessAsync` takes `timeoutSeconds` (default 15s), kills the
  process on timeout. Reuse this for issue creation (network call - give it more headroom,
  e.g. 30s like `commit`).

### Keyboard shortcuts: two layers, no command palette exists yet
- **Global OS-level hotkey** (works even when window isn't focused): only one exists today,
  Ctrl+Shift+Space to bring the window to foreground -
  [Services/HotKeyService.cs](Reemd/Services/HotKeyService.cs). Uses raw
  `RegisterHotKey`/`WM_HOTKEY` via P/Invoke on `user32.dll`. This class is a single-hotkey
  service (one `_hotKeyId`); would need generalizing (or a second instance) to add another
  global hotkey.
- **In-app keyboard shortcuts** (only fire when window/editor has focus): handled in
  [MainWindow.KeyboardMouse.cs](Reemd/MainWindow.KeyboardMouse.cs).
  - `MainWindow_PreviewKeyDown` (line 113): window-level, tunnels before focused control -
    handles things that must work even when WebView2 (preview pane) has focus, e.g.
    Ctrl+Shift+P = force git pull (line 126).
  - `Editor_PreviewKeyDown` (line 243): editor-textbox-level shortcuts (bold, italic, link,
    find/replace, etc).
  - Convention: check `Keyboard.Modifiers == ModifierKeys.Control` for exact-Ctrl-only, or
    bitwise `&` checks when combining with Shift. Every branch sets `e.Handled = true`.
  - **Available shortcut suggestion**: Ctrl+Shift+P is taken (force pull). Ctrl+Shift+I is
    inline-code menu handler name but not bound as a key (Ctrl+I is italic, Ctrl+K is link).
    Something like **Ctrl+Shift+G** (G for "GitHub issue") looks free - not bound anywhere
    in `MainWindow.KeyboardMouse.cs`. Should go in `MainWindow_PreviewKeyDown` (window-level)
    since it's a command that should work regardless of focus, same tier as force-pull.

### Dialog pattern: no XAML dialog windows exist yet - built inline in C#
- Searched for `*Dialog*.xaml` / a `Dialogs/` folder - none exists. `Models/` only has
  `CursorPosition.cs` and `FileEntry.cs`.
- The one existing custom dialog (rename file) is built **entirely in code**, not XAML -
  see [MainWindow.FileOperations.cs](Reemd/MainWindow.FileOperations.cs) lines ~300-420
  (method containing the rename prompt). Pattern:
  - `new Window { Width, Height, WindowStartupLocation = CenterOwner, Owner = this,
    ResizeMode = NoResize, WindowStyle = ToolWindow, Background/Foreground themed to match
    current app theme (reads `bg`/`fg` colors from somewhere above this snippet - check
    `MainWindow.Theme.cs` for the theme color source) }`.
  - Content built as nested `StackPanel`s added via `.Children.Add(...)`.
  - OK button: `IsDefault = true`, click handler sets `dialog.DialogResult = true`.
  - Cancel button: `IsCancel = true`, sets `dialog.DialogResult = false`.
  - `dialog.Loaded` focuses/selects the primary input.
  - Caller does `var result = dialog.ShowDialog(); if (result != true) return;` then reads
    values back off the captured local controls (closures, not a dialog "view model").
  - File-only dialogs (folder picker, "Open Folder") use the standard WPF
    `OpenFolderDialog` / presumably `SaveFileDialog`/`OpenFileDialog` where applicable -
    grep found `OpenFolderDialog` in
    [MainWindow.FolderManagement.cs:244](Reemd/MainWindow.FolderManagement.cs#L244).
- **For this feature**: given 3 fields (repo combo, title textbox, multi-line description)
  plus a reload button and a submit action with async network I/O and error/status
  feedback, a **dedicated XAML Window** (e.g. `NewIssueDialog.xaml` +
  `NewIssueDialog.xaml.cs`) is more maintainable than the inline-code pattern used for the
  simple one-field rename prompt. This would be a new convention (first XAML-based dialog
  in the app) - worth confirming with the user before diverging from the existing
  code-only-dialog style, or alternatively keep it code-built for consistency. Flag this
  choice explicitly next session rather than silently picking one.

### Markdown editor image/link handling to reuse
Two different pieces cover "images and links as our MD editor does":

1. **Toolbar/shortcut-driven insertion** (manual, cursor-position based) -
   [MainWindow.MarkdownEditing.cs](Reemd/MainWindow.MarkdownEditing.cs):
   - `InsertLinkMarkdown()` (line 198): no selection -> inserts
     `[link text](url)` and selects "link text"; with selection -> wraps as
     `[selected](url)` and selects "url" for editing. Bound to Ctrl+K and a context menu
     item.
   - `InsertMarkdownWrapper(delimiter)` (line 141): generic bold/italic/code wrapper, not
     directly relevant to images but same interaction pattern.
   - No dedicated "insert image via file picker" toolbar action exists - images only come
     in via paste/drag-drop (below). If the new issue-description field needs a manual
     "insert image" button (vs. just paste/drop), that would be new code modeled on
     `InsertLinkMarkdown`.

2. **Paste and drag-drop of images** (automatic) -
   [MainWindow.DragDropPaste.cs](Reemd/MainWindow.DragDropPaste.cs), this is the richer
   piece and almost certainly what "as our MD editor does" refers to:
   - `HandlePaste` (line 344): clipboard image bitmap -> saved as PNG to
     `_markdownFolder` as `image-{yyyyMMdd-HHmmss}.png` (dedup via counter suffix), then
     inserts `![name](fileName)` markdown at caret. Clipboard image-URL text -> inserts
     `![Image](url)` directly (no download).
   - `Editor_PreviewDrop` (line 189): dropped image **files** get copied into
     `_markdownFolder` (or referenced in place if already there) and inserted as
     `![name](relativeOrFileName)`; dropped image **URLs** (plain text, or extracted from
     HTML `<img src>`/`<a href>` via `GetImageUrlsFromHtml`, line 111) get inserted as
     `![Image](url)` markdown, not downloaded.
   - `IsImageUrl()` (line 292): extension allowlist check (`.png .jpg .jpeg .gif .webp .svg
     .bmp .ico`), handles query strings and browser-appended title text.
   - **Key mismatch to resolve next session**: this logic saves pasted/dropped images as
     **local files relative to `_markdownFolder`** (`![name](fileName)`), which only makes
     sense when the markdown will live in that same folder (and, per `MainWindow.GitHub.cs`,
     get committed/pushed alongside it via the periodic sync). A GitHub **Issue** body is
     not a file next to other files - a relative path `![x](image-....png)` won't resolve
     unless the image is uploaded somewhere issue bodies can reference (GitHub issue
     comments support pasting images directly in the web UI, which uploads to
     `user-images.githubusercontent.com`, but the `gh issue create` CLI has no equivalent
     upload mechanism). Options to resolve then:
     - (a) Reuse `_markdownFolder`-relative save behavior but then also upload the pasted
       image via `gh api` (GitHub's REST upload endpoint for issue attachments generally
       requires the web UI's asset upload flow, not a documented public API - needs
       verification) - **needs research**, may not be feasible via `gh` CLI alone.
       Skip: research is now in-progress in `../reepolee-dev` via a background agent that
       was targeting the wrong project - do not reuse its output for this task.
     - (b) Simplest v1: only support pasting/dragging **image URLs** (already-hosted images)
       into the issue description, reusing exactly the URL-detection paths of
       `HandlePaste`/`Editor_PreviewDrop` (`IsImageUrl`, `GetImageUrlsFromHtml`), and skip
       local-file/clipboard-bitmap upload entirely for the issue dialog. This avoids needing
       any new upload mechanism and still satisfies "images and links as our MD editor does"
       for the common case of dragging an image from a browser tab.
     - Decide (a) vs (b) with the user before implementing.

### Repo filtering ("reepolee/ree*")
- `gh repo list reepolee --json name --limit 200` returns JSON; filter names with
  `StringComparison.OrdinalIgnoreCase` prefix `"ree"` client-side in C# (simpler and more
  portable across `gh` versions than relying on `-q` jq filtering for prefix matching).
- Cache the filtered list in-memory on `GitHubService` (or directly on the new dialog's
  code-behind/view-model) after first fetch; wire a "Reload" button in the dialog to force
  a re-fetch (bypass cache) - mirrors the user's explicit ask from earlier in this session.

## Suggested shape of the change (not yet implemented)

1. `GitHubService`: add `ListReeRepositoriesAsync(bool forceRefresh = false)` (cached) and
   `CreateIssueAsync(string repoNameWithOwner, string title, string body)`.
2. New dialog (XAML window recommended, see above) with: repo `ComboBox` (+ reload
   `Button`), title `TextBox`, description multi-line `TextBox`/editor control reusing
   paste/drop handlers (extract the URL-only subset of `DragDropPaste` logic into a shared
   helper so both `MainWindow` and the new dialog can call it, rather than duplicating).
3. Wire Ctrl+Shift+G (or user's preferred combo) in `MainWindow_PreviewKeyDown` to open the
   dialog.
4. Submit handler: validate repo+title non-empty, call `CreateIssueAsync`, show success/
   error via the existing `SetStatus(...)` mechanism (used throughout `MainWindow`) or a
   simple result `MessageBox`.

## Decisions (confirmed by user, 2026-08-07)

1. **Dialog**: dedicated XAML window (`NewIssueDialog.xaml` + `.xaml.cs`) - first XAML
   dialog in the app.
2. **Images**: skip entirely for v1. Description is plain text/markdown, no paste/drop
   image handling. May be added later - not in this pass.
3. **Shortcut**: Ctrl+Shift+G, wired in `MainWindow_PreviewKeyDown`.
4. **Reload repos**: plain button in the dialog, no separate shortcut.

## Implementation plan

1. `GitHubService`: add
   - `ListReeRepositoriesAsync(bool forceRefresh = false)` - runs
     `gh repo list reepolee --json name,nameWithOwner --limit 200`, filters names with
     `StringComparison.OrdinalIgnoreCase` prefix `"ree"`, caches result in a
     `_cachedReeRepos` field, `forceRefresh` bypasses cache.
   - `CreateIssueAsync(string repoNameWithOwner, string title, string body)` - runs
     `gh issue create --repo <repo> --title <title> --body-file -` piping body via stdin
     (avoid shell-escaping), 30s timeout like `commit`.
2. `Dialogs/NewIssueDialog.xaml` + `.xaml.cs`: repo `ComboBox` + "Reload" `Button`, title
   `TextBox`, description multi-line `TextBox` (plain, no image handling). OK/Cancel like
   existing dialogs conceptually, but XAML-based. Theme colors sourced same way as rest of
   app (check `MainWindow.Theme.cs`). On load, populate repo combo from cached list (fetch
   if empty).
3. Wire Ctrl+Shift+G in `MainWindow_PreviewKeyDown` (`MainWindow.KeyboardMouse.cs`) to open
   `NewIssueDialog`, `e.Handled = true`.
4. Submit handler in dialog: validate repo + title non-empty, call `CreateIssueAsync`, show
   success/error (status bar via `SetStatus` callback passed in, or simple MessageBox from
   dialog itself - decide during implementation, prefer consistency with `SetStatus` if
   dialog can reach it).
