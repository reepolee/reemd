# Reemd

A fast, cross-platform Markdown editor with live preview, folder browsing, GitHub
sync, and system-wide hotkeys. Built on [.NET 10](https://dotnet.microsoft.com/) and
[Avalonia UI](https://avaloniaui.net/) — runs on **Windows** and **macOS**.

## Features

- **Split-pane editing** — Markdown source on the left, rendered HTML preview on the right, with scroll sync.
- **Folder-based workflow** — point it at a folder of `.md` files and browse/navigate them from the sidebar.
- **GitHub sync** — auto pull-then-push of the folder as a git repo (via the `gh` CLI), plus a manual Pull button (`Ctrl+Shift+P`).
- **New Issue dialog** — file GitHub issues against `reepolee/ree*` repos without leaving the editor (`Ctrl+Alt+I`).
- **Project shortcuts** — up to 9 toolbar buttons that open a project folder, VS Code, and a terminal in one keystroke (`Ctrl+Shift+1..9` by default).
- **Global hotkeys** — toggle the window from anywhere (`Ctrl+Shift+Space`) and a tray/menu-bar icon.
- **LAN clipboard sync** - share text clipboard changes with ReeMD instances on the same local-network channel.
- **Live preview extras** — GitHub-flavored Markdown (Markdig), syntax-highlighted code blocks, and inline local images (drag-drop or paste).
- **Find & replace**, word-wrap toggle, dark/light theme, and per-panel font sizing.

## Install

Install the latest release without cloning or building:

**macOS:**

```bash
curl -fsSL https://raw.githubusercontent.com/reepolee/reemd/main/install.sh | bash
```

Downloads the latest `ReeMD.app` bundle, installs it to `/Applications`, and
launches it. Override the location with `INSTALL_DIR`, e.g.
`INSTALL_DIR=$HOME/Applications curl -fsSL … | bash`.

**Windows:**

```powershell
irm https://raw.githubusercontent.com/reepolee/reemd/main/install.ps1 | iex
```

Downloads the latest `Reemd.exe`, installs it to `~/bin`, and adds that to your
user `PATH`. Override the location with `$env:INSTALL_DIR = "…"` first.

## Requirements

### .NET 10 SDK

The project pins the .NET 10 SDK (`global.json` → `10.0.400`, `rollForward: latestFeature`),
so install a `10.0.x` SDK — any `10.0.x` build satisfies it.

**macOS (Homebrew):**

```bash
brew install --cask dotnet-sdk@10
```

Open a new terminal, then verify:

```bash
dotnet --version   # should print 10.0.x
```

**macOS (no Homebrew):**

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
export PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"
dotnet --version
```

**Windows:** install from <https://dotnet.microsoft.com/download/dotnet/10.0>, or
`winget install Microsoft.DotNet.SDK.10`.

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

## Run on macOS

```bash
git clone https://github.com/reepolee/reemd.git
cd reemd
```

**Quick dev run** — keeps log output in the terminal (handy when debugging hotkeys):

```bash
dotnet run --project Reemd.Avalonia
```

**Self-contained build** — no `dotnet` needed afterward:

```bash
./build.sh publish osx-arm64     # Apple Silicon (use osx-x64 for Intel)
./publish/osx-arm64/Reemd
```

**App bundle** — builds `ReeMD.app`, installs to `/Applications`, and launches:

```bash
./deploy.sh
```

> For troubleshooting, run the binary directly from a terminal (`./publish/osx-arm64/Reemd`)
> rather than via `open`, so error output (e.g. `[Reemd] ...` lines) stays visible.

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
| `PageUp` / `PageDown` | Page up / down in the editor or preview (whichever has focus) |
| `Cmd+Up` / `Cmd+Down` (macOS) | Page up / down on MacBook keyboards (editor or preview) |
| `Alt+Z` | Toggle word wrap |
| `Ctrl+Shift+P` | Force git pull |
| `Ctrl+Plus` / `Ctrl+Minus` / `Ctrl+0` | Adjust active panel font size |

## Project shortcuts

Add up to 9 shortcuts via the ⚙ button in the toolbar. Each shortcut opens a project
folder in VS Code and a terminal in that folder. Launch with `Ctrl+Shift+<number>`
globally, or just `<number>` while the editor is focused.

Shortcut settings (the list **and** the hotkey combo) are stored per-device in a
`<device>.<os>.reemd.projects.json` file at the root of the folder you're editing
(e.g. `comet.win.reemd.projects.json`, `m4mini.macos.reemd.projects.json`), so each
machine keeps its own paths and commands (Windows vs macOS terminal launchers) while
every device's file is committed by the regular GitHub auto-sync. When you open a
repo, Reemd loads *your* device's file, so the toolbar adapts automatically. A legacy
shared `reemd.projects.json` (from before this change) is adopted once and then
deleted. If a repo has no config for your device yet, your most recently saved
shortcuts are used as a fallback. The filename is plain (no dot-prefix, no subfolder)
specifically so common `.gitignore` patterns like `.*` or `.reemd/` can't hide it; if
a repo's `.gitignore` still excludes it (e.g. `*.json`), Reemd shows a status-bar
warning and the shortcuts stay local to that machine.

New shortcuts pre-fill a platform-appropriate command — `code {path} && wt -d {path}`
on Windows, `code {path} && open -a iTerm {path}` on macOS — which you can clear to
fall back to Reemd's built-in VSCode + terminal launch (which actually `cd`s into the
folder).

## LAN clipboard sync

ReeMD checks the operating system text clipboard and shares changes by UDP broadcast with
other ReeMD instances on the same LAN clipboard channel. The channel is shown as `LAN clip:`
in the toolbar and defaults to `ree-md`.

To temporarily share with another group, enter the same channel name on each participating
device and press Enter. Change the value back to your usual device channel to leave that
group. Channel names can contain letters, numbers, dots, dashes, and underscores. Clipboard
sync is text-only, is limited to 48 KB per update, and is not encrypted, so use it only on a
trusted local network.

The status bar shows listener, send, receive, and error events. Select `Log` beside the channel
to open the payload-free `clipboard-sync.log` file when diagnosing a network or firewall issue.

## License

[MIT](LICENSE)
