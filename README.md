# Reemd

A fast, cross-platform Markdown editor with live preview, folder browsing, GitHub
sync, and system-wide hotkeys. Built on [.NET 9](https://dotnet.microsoft.com/) and
[Avalonia UI](https://avaloniaui.net/) — runs on **Windows** and **macOS**.

## Features

- **Split-pane editing** — Markdown source on the left, rendered HTML preview on the right, with scroll sync.
- **Folder-based workflow** — point it at a folder of `.md` files and browse/navigate them from the sidebar.
- **GitHub sync** — auto pull-then-push of the folder as a git repo (via the `gh` CLI), plus a manual Pull button (`Ctrl+Shift+P`).
- **New Issue dialog** — file GitHub issues against `reepolee/ree*` repos without leaving the editor (`Ctrl+Alt+I`).
- **Project shortcuts** — up to 9 toolbar buttons that open a project folder, VS Code, and a terminal in one keystroke (`Ctrl+Shift+1..9` by default).
- **Global hotkeys** — toggle the window from anywhere (`Ctrl+Shift+Space`) and a tray/menu-bar icon.
- **Live preview extras** — GitHub-flavored Markdown (Markdig), syntax-highlighted code blocks, and inline local images (drag-drop or paste).
- **Find & replace**, word-wrap toggle, dark/light theme, and per-panel font sizing.

## Requirements

### .NET 9 SDK

The project pins the .NET 9 SDK (`global.json` → `9.0.316`, `rollForward: latestFeature`),
so install a `9.0.x` SDK — any `9.0.x` build satisfies it.

**macOS (Homebrew):**

```bash
brew install --cask dotnet-sdk@9
```

Open a new terminal, then verify:

```bash
dotnet --version   # should print 9.0.x
```

**macOS (no Homebrew):**

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0
export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"
dotnet --version
```

**Windows:** install from <https://dotnet.microsoft.com/download/dotnet/9.0>, or
`winget install Microsoft.DotNet.SDK.9`.

> If `dotnet` is installed but "not found", restart the terminal or run `source ~/.zshrc`
> (macOS) so the updated `PATH` is picked up.

### `gh` CLI

Used for GitHub sync and the New Issue dialog. macOS: `brew install gh`; Windows:
`winget install GitHub.cli`. Then run `gh auth login`.

## Build & run

```bash
# build (host platform)
./build.sh

# run
dotnet run --project Reemd.Avalonia
```

### Publish

```bash
# Windows (self-contained)
./build.sh publish win-x64      # or win-arm64
./build.ps1 -Release -Publish

# macOS (self-contained)
./build.sh publish osx-arm64    # Apple Silicon
./build.sh publish osx-x64      # Intel

# macOS .app bundle + install + launch
./deploy.sh
```

## Hotkeys

### Global (work from anywhere)

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+Space` | Show/hide the Reemd window |
| `Ctrl+Alt+I` | New GitHub issue |
| `Ctrl+Shift+1`..`9` | Launch project shortcut 1–9 (modifiers configurable) |

### In the editor

| Shortcut | Action |
|---|---|
| `Ctrl+S` / `Ctrl+N` | Save / new file |
| `Ctrl+B` / `Ctrl+I` | Bold / italic |
| `Ctrl+K` | Insert link |
| `Ctrl+Shift+C` / `Ctrl+Shift+I` | Code block / inline code |
| `Ctrl+F` / `Ctrl+H` | Find / replace |
| `F3` / `Shift+F3` | Find next / previous |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous file |
| `Alt+Up` / `Alt+Down` | Move line up / down |
| `Alt+Z` | Toggle word wrap |
| `Ctrl+Shift+P` | Force git pull |
| `Ctrl+Plus` / `Ctrl+Minus` / `Ctrl+0` | Adjust active panel font size |

## Project shortcuts

Add up to 9 shortcuts via the ⚙ button in the toolbar. Each shortcut opens a project
folder in VS Code and a terminal in that folder. Launch with `Ctrl+Shift+<number>`
globally, or just `<number>` while the editor is focused.

## License

[MIT](LICENSE)
