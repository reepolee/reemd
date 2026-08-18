# Reemd — macOS support via Avalonia UI

**Status: ported (2026-08-18), Windows build + macOS cross-compile green. Runtime verification on macOS pending.**

The original Windows-only WPF app (`Reemd/`) has been ported to **Avalonia UI** in
`Reemd.Avalonia/` so a single codebase targets Windows and macOS (and, with minor work,
Linux). This is a UI migration: the markdown/GitHub/sync logic was unchanged.

## What changed

| Area | WPF | Avalonia |
|---|---|---|
| UI framework | `net9.0-windows`, WPF XAML | `net9.0`, Avalonia 12 XAML (`.axaml`) |
| Preview pane | WebView2 (`SetVirtualHostNameToFolderMapping`) | `Avalonia.Controls.WebView` `NativeWebView` (WebView2 on Windows, WKWebView on macOS) |
| Local images in preview | virtual host `reemd.local` | base64 data-URI inlining (cross-platform, no host mapping) |
| Tray icon | `Hardcodet.NotifyIcon.Wpf` | Avalonia `TrayIcon` |
| Global hotkeys | `RegisterHotKey` (user32) | Windows `RegisterHotKey` + macOS Carbon `RegisterEventHotKey` |
| File/folder dialogs | `Microsoft.Win32` | `StorageProvider` |
| Clipboard / drag-drop | WPF `Clipboard` / `DataObject` | Avalonia 12 `IDataTransfer` / `IAsyncDataTransfer` |
| Process launching (VSCode/terminal/cmd) | hard-coded Windows | `ProcessLauncher` (Windows + macOS paths) |

Reusable logic (unchanged, byte-for-byte where possible): `MarkdownConverter` (plus image
inlining), `GitHubService`, `SyncLogger`, `Config`, `Models/*`, and all `MainWindow.*`
business logic (file ops, sync, find/replace, markdown editing, settings, projects).

## Build & run

```bash
# Windows
dotnet build Reemd.Avalonia/Reemd.csproj
./build.sh publish win-x64      # self-contained Windows exe

# macOS
./build.sh publish osx-arm64    # Apple Silicon
./build.sh publish osx-x64      # Intel Mac
```

Or PowerShell: `.\build.ps1 -Release -Publish -Runtime osx-arm64`.

The publish output is a native executable (framework-dependent or self-contained).

To deploy to macOS (publish + bundle into `Reemd.app` + install + launch):

```bash
./deploy.sh              # auto-detect arch, install to ~/Applications, launch
./deploy.sh --system     # install to /Applications
./deploy.sh --no-run     # install without launching
```

`deploy.sh` generates a `Reemd.app` bundle with an `Info.plist`; an `icon.icns` is optional
(place one at the repo root or in `Reemd.Avalonia/` to embed it).

## Verified

- `dotnet build` (Windows) — 0 warnings, 0 errors.
- `dotnet build -c Release -r osx-arm64` and `osx-x64` (cross-compile from Windows) — clean.

## Not yet verified (needs a real Mac)

1. **Runtime behavior** — WKWebView preview, `TrayIcon` (NSStatusItem), Carbon global
   hotkeys, `open`/`Terminal.app` project launching. These compile but cannot be exercised
   from Windows.
2. **Carbon hotkeys** (`Services/HotKeyService.cs`) — correct P/Invoke signatures, but
   `RegisterEventHotKey` + `InstallEventHandler` need a live run-loop test.
3. **Tray/menu-bar icon** — uses the `.ico`; macOS expects a monochrome template PNG/`.icns`
   for a proper menu-bar icon.
4. **`.app` bundle** — needs an `Info.plist` + `icon.icns` + codesign step.

## Behavior changes vs. WPF (intentional)

- Local images in the preview are base64-inlined (self-contained HTML) instead of served via
  a virtual host. Equivalent output, slightly larger HTML for big images.
- Drag-and-drop now reads text/files via the new Avalonia data-transfer API. Browser HTML
  image drags (`<img src>`) and Windows-only OLE URL formats are not parsed — image *file*
  drops and image *URL* text drops still work.
- Project reordering in the editor dialog uses the ▲/▼ buttons (drag-reorder not ported).
- Editor cursor-position readout is driven by key/pointer/text events (Avalonia `TextBox` has
  no `SelectionChanged` event).

## Removing the old WPF project

`Reemd/` (WPF) is superseded by `Reemd.Avalonia/`. Delete it (and update `build.ps1`'s
history references) after the Avalonia app is verified on both Windows and macOS. The
uncommitted project-shortcuts work that lived in `Reemd/` was fully ported.
