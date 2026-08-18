# Install ReeMD from the latest GitHub Release.
# Usage: irm https://raw.githubusercontent.com/reepolee/reemd/main/install.ps1 | iex

$AppName = "Reemd"
$Owner = "reepolee"
$Repo = "reemd"
$InstallDir = if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { Join-Path $HOME "bin" }

# ──────────────────────────────────────────────
# Detect platform
# ──────────────────────────────────────────────

$arch = $env:PROCESSOR_ARCHITECTURE
switch ($arch) {
	'AMD64' { $assetName = "$AppName-windows-x64.zip" }
	'ARM64' { $assetName = "$AppName-windows-arm64.zip" }
	default { Write-Error "Unsupported architecture: $arch"; exit 1 }
}

# ──────────────────────────────────────────────
# Download
# ──────────────────────────────────────────────

$downloadUrl = "https://github.com/$Owner/$Repo/releases/latest/download/$assetName"
$tmpZip = Join-Path $env:TEMP $assetName

Write-Host "→ Downloading $assetName ..."
try {
	Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpZip -UseBasicParsing
} catch {
	Write-Error "Download failed: $_"
	exit 1
}

# ──────────────────────────────────────────────
# Extract
# ──────────────────────────────────────────────

$extractDir = Join-Path $env:TEMP "$AppName-extract"
if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
Expand-Archive -Path $tmpZip -DestinationPath $extractDir -Force
Remove-Item $tmpZip -Force

# ──────────────────────────────────────────────
# Install
# ──────────────────────────────────────────────

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = Join-Path $InstallDir "$AppName.exe"
Copy-Item (Join-Path $extractDir "$AppName.exe") $target -Force
Remove-Item $extractDir -Recurse -Force

Write-Host "  Installed to $target"

# ──────────────────────────────────────────────
# PATH check
# ──────────────────────────────────────────────

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$paths = $userPath -split ";"

if ($paths -notcontains $InstallDir) {
	$newPath = if ([string]::IsNullOrWhiteSpace($userPath)) {
		$InstallDir
	} else {
		"$userPath;$InstallDir"
	}

	[Environment]::SetEnvironmentVariable("Path", $newPath, "User")
	Write-Host "  Added $InstallDir to user PATH"
	Write-Host ""
	Write-Host "Restart your terminal to use $AppName"
}

Write-Host ""
Write-Host "✅ $AppName installed!"
