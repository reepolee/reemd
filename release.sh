#!/usr/bin/env bash
# Release script — builds ALL platform executables from a single machine and
# publishes them as one GitHub Release.
#
# ReeMD is a .NET 9 (Avalonia) app, so cross-compilation is native to the SDK:
# one `dotnet publish` per RID produces each platform's executable. No extra
# linkers are needed (unlike the Rust toolchain in reettier, which needs Zig +
# xwin).
#
# Targets:
#   Windows x64/arm64  → Reemd-windows-{x64,arm64}.zip   (Reemd.exe + native DLLs)
#   macOS   x64/arm64  → Reemd-macos-{x64,arm64}.zip     (ReeMD.app bundle)
#
# Usage: bash release.sh [--draft] [--minor] [--force]
#   --draft  Create the release as a draft (default: published)
#   --minor  Bump the month component instead of the patch version (default: patch)
#   --force  Release the current version even if it is ahead of the tag
#
# Prerequisites (one-time, on the release machine):
#   .NET 9 SDK  → https://dotnet.microsoft.com/download/dotnet/9.0
#   gh CLI authenticated → `gh auth login`
#   zip (macOS ships it; `brew install zip` if missing)

set -euo pipefail

# Report the failing command, line, and exit code so callers surface a real error.
trap 'ec=$?; echo "ERROR: release.sh failed at line $LINENO: $BASH_COMMAND (exit $ec)" >&2' ERR

APP="Reemd"
BUNDLE="ReeMD"
OWNER="reepolee"
REPO="reemd"
PROJECT="Reemd.Avalonia/Reemd.csproj"
OUT="dist"

# ──────────────────────────────────────────────
# Validate prerequisites
# ──────────────────────────────────────────────

for cmd in dotnet gh zip; do
	if ! command -v "$cmd" &>/dev/null; then
		echo "ERROR: $cmd not found." >&2
		exit 1
	fi
done

if ! gh auth status &>/dev/null; then
	echo "ERROR: gh CLI is not authenticated. Run: gh auth login" >&2
	exit 1
fi

# ──────────────────────────────────────────────
# Parse flags
# ──────────────────────────────────────────────

draft_flag=""
minor_bump=false
force=false

for arg in "$@"; do
	case "$arg" in
		--draft) draft_flag="--draft" ;;
		--minor) minor_bump=true ;;
		--force) force=true ;;
	esac
done

# ──────────────────────────────────────────────
# Version helpers (date-based YY.MM.patch, same scheme as reettier)
# ──────────────────────────────────────────────

bump_patch() {
	local current="$1" year="${1%%.*}" rest="${1#*.}" month patch
	month="${rest%%.*}"
	patch="${rest#*.}"
	echo "$year.$month.$((10#$patch + 1))"
}

bump_minor() {
	local current="$1" year="${1%%.*}" rest="${1#*.}" month patch new_month
	month="${rest%%.*}"
	patch="${rest#*.}"
	new_month=$((10#$month + 1))
	if [ "$new_month" -gt 12 ]; then
		year=$((10#$year + 1))
		new_month=1
	fi
	printf "%s.%02d.0\n" "$year" "$new_month"
}

current_release_version() {
	printf "%02d.%d.0\n" "$(date +%y)" "$((10#$(date +%m)))"
}

format_release_version() {
	local current="$1" year month patch rest
	year="${current%%.*}"
	rest="${current#*.}"
	month="${rest%%.*}"
	patch="${rest#*.}"
	printf "%s.%02d.%s\n" "$year" "$((10#$month))" "$patch"
}

# Returns 0 (true) if $1 is a greater version than $2
version_gt() {
	local a=(${1//./ }) b=(${2//./ }) ai bi i
	for i in 0 1 2; do
		ai=$((10#${a[$i]:-0}))
		bi=$((10#${b[$i]:-0}))
		[ "$ai" -gt "$bi" ] && return 0
		[ "$ai" -lt "$bi" ] && return 1
	done
	return 1
}

# Read and possibly bump the version in the csproj.
version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$PROJECT" | head -1)
if [ -z "$version" ]; then
	echo "ERROR: no <Version> in $PROJECT" >&2
	exit 1
fi

# ──────────────────────────────────────────────
# Detect code changes since last release
# ──────────────────────────────────────────────

git fetch --tags 2>/dev/null || true
latest_tag=$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || echo "")

do_bump=false
if [ -n "$latest_tag" ]; then
	tag_version="${latest_tag#v}"
	new_commits=$(git rev-list HEAD "^$latest_tag" --count 2>/dev/null || echo 0)
	if [ "$new_commits" -gt 0 ]; then
		if [ "$force" = true ]; then
			new_version="$version"
		elif [ "$minor_bump" = true ]; then
			new_version=$(bump_minor "$version")
		else
			candidate=$(current_release_version)
			if version_gt "$candidate" "$version"; then
				new_version="$candidate"
			else
				new_version=$(bump_patch "$version")
			fi
		fi

		if [ "$new_version" != "$version" ]; then
			sed -i.bak "s|<Version>$version</Version>|<Version>$new_version</Version>|" "$PROJECT"
			rm -f "$PROJECT.bak"
			version="$new_version"
			do_bump=true
		fi
	fi
fi

release_version=$(format_release_version "$version")
tag="v$release_version"

echo "═══ ReeMD release $release_version (all targets) ═══"
if [ "$do_bump" = true ]; then
	echo "  (Bumped csproj to $version)"
fi

# ──────────────────────────────────────────────
# Build (all targets, cross-compiled from this one machine)
# ──────────────────────────────────────────────

# rid:asset-basename
targets=(
	"win-x64:${APP}-windows-x64"
	"win-arm64:${APP}-windows-arm64"
	"osx-x64:${APP}-macos-x64"
	"osx-arm64:${APP}-macos-arm64"
)

rm -rf "$OUT"
built_assets=()

for entry in "${targets[@]}"; do
	rid="${entry%%:*}"
	base="${entry#*:}"
	echo ""
	echo "→ Publishing $rid..."

	dotnet publish "$PROJECT" \
		--configuration Release \
		--runtime "$rid" \
		--self-contained true \
		--output "$OUT/$rid" \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-p:PublishTrimmed=false \
		-p:DebugType=none \
		-p:DebugSymbols=false

	# Native runtime packages (SkiaSharp, HarfBuzz) ship their own .pdb debug
	# symbols that DebugType=none doesn't strip; they roughly double the zip.
	find "$OUT/$rid" -name '*.pdb' -delete

	zip_file="$base.zip"

	if [[ "$rid" == win-* ]]; then
		# Windows: zip the single-file exe + native DLLs together.
		(cd "$OUT/$rid" && zip -q -r "../$zip_file" .)
	else
		# macOS: wrap the publish output in a .app bundle, then zip it.
		stage="$OUT/$base"
		appdir="$stage/$BUNDLE.app"
		mkdir -p "$appdir/Contents/MacOS" "$appdir/Contents/Resources"
		cp -R "$OUT/$rid/." "$appdir/Contents/MacOS/"

		cat > "$appdir/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$BUNDLE</string>
    <key>CFBundleDisplayName</key>
    <string>$BUNDLE</string>
    <key>CFBundleIdentifier</key>
    <string>com.reepolee.reemd</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>$APP</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
</dict>
</plist>
EOF

		# Embed an icon if one exists (macOS .app icons use .icns).
		if [ -f "$(dirname "$PROJECT")/icon.icns" ]; then
			cp "$(dirname "$PROJECT")/icon.icns" "$appdir/Contents/Resources/icon.icns"
		fi

		(cd "$stage" && zip -q -r "../$zip_file" "$BUNDLE.app")
	fi

	built_assets+=("./$OUT/$zip_file#$zip_file")
	echo "  → $zip_file"
done

# ──────────────────────────────────────────────
# Commit version bump
# ──────────────────────────────────────────────

if [ "$do_bump" = true ]; then
	echo ""
	echo "→ Committing version bump..."
	git add "$PROJECT"
	git commit -m "Bump version to $release_version"
	echo "  Committed: Bump version to $release_version"
fi

# ──────────────────────────────────────────────
# Create and push git tag
# ──────────────────────────────────────────────

echo ""
echo "→ Tagging $tag..."
if git rev-parse "$tag" >/dev/null 2>&1; then
	echo "  Tag $tag already exists locally."
else
	git tag "$tag"
	echo "  Created tag $tag locally."
fi

if [ "$do_bump" = true ]; then
	echo "  Pushing version bump commit..."
	git push origin HEAD
fi
echo "  Pushing tag $tag to origin..."
git push origin "$tag"

# ──────────────────────────────────────────────
# Create or upload to GitHub Release
# ──────────────────────────────────────────────

echo ""
echo "→ Publishing release $tag..."

if gh release view "$tag" >/dev/null 2>&1; then
	echo "  Release $tag already exists. Uploading assets..."
	gh release upload "$tag" "${built_assets[@]}" --clobber
else
	echo "  Creating release $tag..."
	gh release create "$tag" \
		"${built_assets[@]}" \
		--title "$tag" \
		--generate-notes \
		$draft_flag
fi

# ──────────────────────────────────────────────
# Clean up local build artifacts
# ──────────────────────────────────────────────
# The zips and staging dirs are now on the release, so drop the local copies.
# This only runs after a successful upload — a failed release keeps $OUT
# intact so it can be retried without rebuilding.

echo ""
echo "→ Removing local build artifacts ($OUT)..."
rm -rf "$OUT"

# ──────────────────────────────────────────────
# Done
# ──────────────────────────────────────────────

echo ""
echo "✅ Done! Released ${#targets[@]} assets → $tag"
echo "   View at: https://github.com/$OWNER/$REPO/releases/tag/$tag"
