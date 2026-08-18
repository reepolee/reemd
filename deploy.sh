#!/usr/bin/env bash
# Deploy Reemd to macOS:
#   1. Publish a self-contained build for the host architecture (or --arch).
#   2. Bundle the publish output into a proper Reemd.app (Info.plist + optional icon).
#   3. Install to ~/Applications (default) or /Applications (--system, needs sudo).
#   4. Launch the app (skip with --no-run).
#
# Usage:
#   ./deploy.sh               # auto-detect Apple Silicon / Intel
#   ./deploy.sh --arch x64    # force Intel build
#   ./deploy.sh --system      # install to /Applications
#   ./deploy.sh --no-run      # don't launch after installing
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_NAME="ReeMD"
EXECUTABLE="Reemd"

ARCH=""
SYSTEM=0
RUN=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --system) SYSTEM=1; shift ;;
    --no-run) RUN=0; shift ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
done

# Detect host architecture.
if [ -z "$ARCH" ]; then
  case "$(uname -m)" in
    arm64)  ARCH="arm64" ;;
    x86_64) ARCH="x64" ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
  esac
fi

RID="osx-${ARCH}"

# Stop any running instance so the bundle can be replaced cleanly.
pkill -x "$EXECUTABLE" 2>/dev/null || true

# 1. Publish (single source of truth for publish flags lives in build.sh).
"$SCRIPT_DIR/build.sh" publish "$RID"

PUBLISH_DIR="$SCRIPT_DIR/publish/$RID"
BUNDLE="$SCRIPT_DIR/dist/$APP_NAME.app"
CONTENTS="$BUNDLE/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"

# 2. Bundle the publish output into Reemd.app.
rm -rf "$BUNDLE"
mkdir -p "$MACOS" "$RESOURCES"
cp -R "$PUBLISH_DIR/." "$MACOS/"

# Icon (optional — macOS expects .icns; generate it from icon.ico separately if needed).
ICON_PLIST=""
if [ -f "$SCRIPT_DIR/Reemd.Avalonia/icon.icns" ]; then
  cp "$SCRIPT_DIR/Reemd.Avalonia/icon.icns" "$RESOURCES/icon.icns"
  ICON_PLIST="    <key>CFBundleIconFile</key>
    <string>icon.icns</string>"
elif [ -f "$SCRIPT_DIR/icon.icns" ]; then
  cp "$SCRIPT_DIR/icon.icns" "$RESOURCES/icon.icns"
  ICON_PLIST="    <key>CFBundleIconFile</key>
    <string>icon.icns</string>"
else
  echo "Note: no icon.icns found — Reemd.app will use the default app icon."
fi

cat > "$CONTENTS/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>com.reemd.app</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
$ICON_PLIST</dict>
</plist>
EOF

# 3. Install.
if [ "$SYSTEM" -eq 1 ]; then
  INSTALL_DIR="/Applications"
  echo "Installing to /Applications (may prompt for password)..."
  sudo cp -R "$BUNDLE" "$INSTALL_DIR/"
else
  INSTALL_DIR="$HOME/Applications"
  mkdir -p "$INSTALL_DIR"
  rm -rf "$INSTALL_DIR/$APP_NAME.app"
  cp -R "$BUNDLE" "$INSTALL_DIR/"
fi

echo "Installed $APP_NAME.app to $INSTALL_DIR"

# 4. Launch.
if [ "$RUN" -eq 1 ]; then
  open "$INSTALL_DIR/$APP_NAME.app"
  echo "Launched $APP_NAME"
fi
