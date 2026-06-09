$BinName = "Reemd.exe"
$InstallDir = Join-Path $HOME "bin"
$Target = Join-Path $InstallDir $BinName

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Copy-Item ".\dist\$BinName" $Target -Force

$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")

$Paths = $UserPath -split ";"

if ($Paths -notcontains $InstallDir) {
	$NewPath = if ([string]::IsNullOrWhiteSpace($UserPath)) {
		$InstallDir
	}
 else {
		"$UserPath;$InstallDir"
	}

	[Environment]::SetEnvironmentVariable(
		"Path",
		$NewPath,
		"User"
	)

	Write-Host "Added $InstallDir to user PATH"
}
else {
	Write-Host "$InstallDir already in PATH"
}

Write-Host "Installed to $Target"
