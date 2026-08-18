#!/usr/bin/env bash
# Install ReeMD from the latest GitHub Release.
# Usage: curl -fsSL https://raw.githubusercontent.com/reepolee/reemd/main/install.sh | bash

set -euo pipefail

APP="Reemd"
BUNDLE="ReeMD"
OWNER="reepolee"
REPO="reemd"
INSTALL_DIR="${INSTALL_DIR:-$HOME/Applications}"

# ──────────────────────────────────────────────
# Detect platform
# ──────────────────────────────────────────────

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
	Darwin)
		case "$arch" in
			arm64|aarch64) asset="${APP}-macos-arm64.zip" ;;
			x86_64)        asset="${APP}-macos-x64.zip" ;;
			*) echo "Unsupported macOS architecture: $arch" >&2; exit 1 ;;
		esac
		;;
	*)
		echo "Unsupported OS: $os" >&2
		echo "ReeMD ships Windows and macOS builds." >&2
		echo "Windows users: run the PowerShell installer instead:" >&2
		echo "  irm https://raw.githubusercontent.com/$OWNER/$REPO/main/install.ps1 | iex" >&2
		exit 1
		;;
esac

# ──────────────────────────────────────────────
# Download
# ──────────────────────────────────────────────

download_url="https://github.com/$OWNER/$REPO/releases/latest/download/$asset"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "→ Downloading $asset..."
if command -v curl &>/dev/null; then
	curl -fsSL "$download_url" -o "$tmp/$asset"
elif command -v wget &>/dev/null; then
	wget -q "$download_url" -O "$tmp/$asset"
else
	echo "ERROR: Neither curl nor wget found." >&2
	exit 1
fi

# ──────────────────────────────────────────────
# Install
# ──────────────────────────────────────────────

echo "→ Installing to $INSTALL_DIR..."
unzip -q "$tmp/$asset" -d "$tmp"
mkdir -p "$INSTALL_DIR"
rm -rf "$INSTALL_DIR/$BUNDLE.app"
cp -R "$tmp/$BUNDLE.app" "$INSTALL_DIR/"

echo "✅ Installed $BUNDLE.app to $INSTALL_DIR"
open "$INSTALL_DIR/$BUNDLE.app"
