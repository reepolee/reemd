#!/usr/bin/env bash
# Builds/publishes the Reemd markdown editor (Avalonia UI, cross-platform).
#
# Usage:
#   ./build.sh                          # build Debug (host RID)
#   ./build.sh release                  # build Release
#   ./build.sh publish osx-arm64        # self-contained publish for Apple Silicon
#   ./build.sh publish osx-x64          # self-contained publish for Intel Mac
#   ./build.sh publish win-x64          # self-contained publish for Windows x64
#   ./build.sh publish win-arm64        # self-contained publish for Windows ARM64
set -euo pipefail

MODE="${1:-build}"
RUNTIME="${2:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/Reemd.Avalonia/Reemd.csproj"
OUTPUT="$SCRIPT_DIR/publish"

case "$MODE" in
  build)
    CONFIG="Debug"
    if [ "${2:-}" = "release" ]; then CONFIG="Release"; fi
    dotnet build "$PROJECT" --configuration "$CONFIG"
    ;;
  release)
    dotnet build "$PROJECT" --configuration Release
    ;;
  publish)
    if [ -z "$RUNTIME" ]; then
      echo "Usage: ./build.sh publish <runtime> (osx-arm64|osx-x64|win-x64|win-arm64)" >&2
      exit 1
    fi
    dotnet publish "$PROJECT" \
      --configuration Release \
      --runtime "$RUNTIME" \
      --self-contained true \
      --output "$OUTPUT/$RUNTIME" \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:PublishTrimmed=false
    ;;
  *)
    echo "Unknown mode: $MODE" >&2
    exit 1
    ;;
esac
