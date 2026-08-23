using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reemd.Services;

/// <summary>Checks GitHub Releases and installs a newer ReeMD build after shutdown.</summary>
public static class AutoUpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/reepolee/reemd/releases/latest";
    private static readonly HttpClient HttpClient = new();

    static AutoUpdateService()
    {
        var userAgent = HttpClient.DefaultRequestHeaders.UserAgent;
        userAgent.ParseAdd("ReeMD-Updater");
    }

    public static async Task<UpdateRelease?> GetLatestReleaseAsync()
    {
        using var response = await HttpClient.GetAsync(ReleasesUrl);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("GitHub returned an invalid release response.");

        var releaseVersionText = release.TagName.TrimStart('v');
        if (!Version.TryParse(releaseVersionText, out var releaseVersion))
            throw new InvalidOperationException("The latest GitHub release has an invalid version.");

        var assembly = Assembly.GetExecutingAssembly();
        var assemblyName = assembly.GetName();
        var currentVersion = assemblyName.Version
            ?? throw new InvalidOperationException("The installed ReeMD version is unavailable.");

        if (releaseVersion <= currentVersion)
            return null;

        var assetName = GetAssetName();
        var asset = release.Assets.FirstOrDefault(item => item.Name == assetName)
            ?? throw new InvalidOperationException($"The latest release does not include {assetName}.");

        if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            throw new InvalidOperationException("The latest release has no download URL.");

        return new UpdateRelease(releaseVersionText, asset.BrowserDownloadUrl);
    }

    public static async Task<StagedUpdate> DownloadAndStageAsync(UpdateRelease release)
    {
        var installPath = GetInstallPath();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"Reemd-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(temporaryDirectory, "update.zip");
        var extractedPath = Path.Combine(temporaryDirectory, "extracted");

        Directory.CreateDirectory(temporaryDirectory);
        using var response = await HttpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var archiveStream = await response.Content.ReadAsStreamAsync();
        await using var outputStream = File.Create(archivePath);
        await archiveStream.CopyToAsync(outputStream);

        ZipFile.ExtractToDirectory(archivePath, extractedPath);

        var stagedPath = OperatingSystem.IsMacOS()
            ? Path.Combine(extractedPath, "ReeMD.app")
            : extractedPath;
        var stagedExecutablePath = OperatingSystem.IsMacOS()
            ? Path.Combine(stagedPath, "Contents", "MacOS", "Reemd")
            : Path.Combine(stagedPath, "Reemd.exe");

        if (!File.Exists(stagedExecutablePath))
            throw new InvalidOperationException("The update archive does not contain a valid ReeMD application.");

        return new StagedUpdate(installPath, stagedPath);
    }

    public static void StartInstaller(StagedUpdate update)
    {
        var processId = Environment.ProcessId;
        if (OperatingSystem.IsMacOS())
        {
            StartMacInstaller(processId, update);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            StartWindowsInstaller(processId, update);
            return;
        }

        throw new PlatformNotSupportedException("ReeMD updates are available on macOS and Windows only.");
    }

    private static string GetAssetName()
    {
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "Reemd-macos-arm64.zip"
                : "Reemd-macos-x64.zip";

        if (OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "Reemd-windows-arm64.zip"
                : "Reemd-windows-x64.zip";

        throw new PlatformNotSupportedException("ReeMD updates are available on macOS and Windows only.");
    }

    private static string GetInstallPath()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The ReeMD executable path is unavailable.");

        if (OperatingSystem.IsWindows())
        {
            return Path.GetDirectoryName(executablePath)
                ?? throw new InvalidOperationException("The ReeMD install directory is unavailable.");
        }

        var macOsDirectory = Directory.GetParent(executablePath)
            ?? throw new InvalidOperationException("The ReeMD application bundle is unavailable.");
        var contentsDirectory = macOsDirectory.Parent;
        var applicationDirectory = contentsDirectory?.Parent;

        if (macOsDirectory.Name != "MacOS" || contentsDirectory?.Name != "Contents" ||
            applicationDirectory == null || !applicationDirectory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ReeMD must be installed as an application bundle before it can update itself.");
        }

        return applicationDirectory.FullName;
    }

    private static void StartMacInstaller(int processId, StagedUpdate update)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"Reemd-install-{Guid.NewGuid():N}.sh");
        var script = "#!/bin/sh\n" +
            "while kill -0 \"$1\" 2>/dev/null; do sleep 1; done\n" +
            "rm -rf \"$2\"\n" +
            "mv \"$3\" \"$2\"\n" +
            "open \"$2\"\n" +
            "rm -- \"$0\"\n";
        File.WriteAllText(scriptPath, script);

        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add(update.InstallPath);
        startInfo.ArgumentList.Add(update.StagedPath);
        Process.Start(startInfo);
    }

    private static void StartWindowsInstaller(int processId, StagedUpdate update)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"Reemd-install-{Guid.NewGuid():N}.cmd");
        var script = "@echo off\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"PID eq %1\" 2>NUL | find \"%1\" >NUL\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >NUL\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            "rmdir /s /q \"%~2\"\r\n" +
            "move \"%~3\" \"%~2\"\r\n" +
            "start \"\" \"%~2\\Reemd.exe\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(scriptPath, script);

        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add(update.InstallPath);
        startInfo.ArgumentList.Add(update.StagedPath);
        Process.Start(startInfo);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}

public sealed record UpdateRelease(string Version, string DownloadUrl);

public sealed record StagedUpdate(string InstallPath, string StagedPath);
