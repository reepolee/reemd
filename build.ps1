<#
.SYNOPSIS
    Builds or publishes the Reemd markdown editor.
.DESCRIPTION
    Default mode: restores packages and builds with dotnet.
    With -Publish: produces a self-contained Release build in a publish/ directory,
    bundling the .NET runtime so the app runs on any Windows machine.
.PARAMETER Release
    Build in Release configuration (Debug is the default).
.PARAMETER Publish
    Publish as a self-contained executable instead of just building.
.PARAMETER OutputDir
    Target directory for publish output (default: publish/). Only used with -Publish.
.PARAMETER Runtime
    Target runtime identifier (default: win-x64). Only used with -Publish.
.PARAMETER NoBuild
    Skip the build step during publish (use previously built artifacts). Only used with -Publish.
.EXAMPLE
    .\build.ps1
    .\build.ps1 -Release
    .\build.ps1 -Publish
    .\build.ps1 -Release -Publish -OutputDir dist -Runtime win-arm64
    .\build.ps1 -Publish -NoBuild
#>

param(
    [switch]$Release,
    [switch]$Publish,
    [string]$OutputDir = "publish",
    [string]$Runtime = "win-x64",
    [switch]$NoBuild
)

$ProjectDir = Join-Path $PSScriptRoot "Reemd"
$ProjectFile = Join-Path $ProjectDir "Reemd.csproj"
$Configuration = if ($Release) { "Release" } else { "Debug" }

# ── Kill any running instance ──────────────────────────────────────────────
$proc = Get-Process -Name "Reemd" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Stopping running Reemd process(es)..." -ForegroundColor Yellow
    $proc | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# ── Restore ────────────────────────────────────────────────────────────────
Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore $ProjectFile 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Package restore failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# ── Publish mode ───────────────────────────────────────────────────────────
if ($Publish) {
    $OutputPath = Join-Path $PSScriptRoot $OutputDir

    Write-Host "=== Publishing Reemd ($Configuration, $Runtime) ===" -ForegroundColor Cyan
    Write-Host "  Output:  $OutputPath"
    Write-Host ""

    $publishArgs = @(
        $ProjectFile,
        "--configuration", $Configuration,
        "--runtime", $Runtime,
        "--self-contained", "true",
        "--output", $OutputPath,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:AllowedReferenceRelatedFileExtensions=.dll"
    )

    if ($NoBuild) {
        $publishArgs += "--no-build"
    } else {
        $publishArgs += "--no-restore"
    }

    Write-Host "Publishing..." -ForegroundColor Cyan
    dotnet publish @publishArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "PUBLISH FAILED (exit code: $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    Write-Host ""
    Write-Host "PUBLISH SUCCEEDED" -ForegroundColor Green
    Write-Host "Output: $OutputPath" -ForegroundColor Green

    # Show published files (excluding small artifacts)
    $files = Get-ChildItem $OutputPath -File | Where-Object { $_.Length -gt 1KB } | Sort-Object Length -Descending
    Write-Host ""
    Write-Host "Published files:" -ForegroundColor Cyan
    $files | ForEach-Object {
        $sizeStr = if ($_.Length -gt 1MB) {
            "{0:N1} MB" -f ($_.Length / 1MB)
        } else {
            "{0:N0} KB" -f ($_.Length / 1KB)
        }
        Write-Host "  $($_.Name) - $sizeStr" -ForegroundColor Gray
    }

    exit 0
}

# ── Build mode (default) ───────────────────────────────────────────────────
Write-Host "=== Building Reemd ($Configuration) ===" -ForegroundColor Cyan
Write-Host ""

Write-Host "Building..." -ForegroundColor Cyan
dotnet build $ProjectFile --no-restore --configuration $Configuration 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED (exit code: $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "BUILD SUCCEEDED ($Configuration)" -ForegroundColor Green
