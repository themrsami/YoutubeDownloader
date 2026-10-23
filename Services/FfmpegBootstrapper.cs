using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg.Downloader;

namespace PremiumYoutubeDownloader.Services;

public static class FfmpegBootstrapper
{
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);

    public static bool TryConfigureExisting(out string directory)
    {
        foreach (var candidate in GetBundledDirectories().Concat(GetCandidateToolDirectories()).Distinct(PathComparer))
        {
            if (DirectoryHasFfmpeg(candidate))
            {
                Configure(candidate);
                directory = candidate;
                return true;
            }
        }

        directory = string.Empty;
        return false;
    }

    public static async Task<string> EnsureFfmpegCliPathAsync(Action<string>? reportStatus = null, CancellationToken cancellationToken = default)
    {
        var directory = await EnsureAvailableAsync(reportStatus, cancellationToken);
        var ffmpegPath = GetToolPath(directory, "ffmpeg");

        if (!File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException($"FFmpeg was configured in '{directory}', but '{ffmpegPath}' does not exist.");
        }

        EnsureExecutableBits(ffmpegPath);
        return ffmpegPath;
    }

    public static async Task<string> EnsureAvailableAsync(Action<string>? reportStatus = null, CancellationToken cancellationToken = default)
    {
        await BootstrapLock.WaitAsync(cancellationToken);
        try
        {
            if (TryConfigureExisting(out var existingDirectory))
            {
                return existingDirectory;
            }

            reportStatus?.Invoke("Downloading FFmpeg for this device...");
            Directory.CreateDirectory(AppPaths.FfmpegDirectory);

            var progress = reportStatus == null
                ? null
                : new Progress<Xabe.FFmpeg.ProgressInfo>(info =>
                {
                    if (info.TotalBytes > 0)
                    {
                        var percent = Math.Clamp(info.DownloadedBytes * 100.0 / info.TotalBytes, 0, 100);
                        reportStatus($"Downloading FFmpeg for this device... {percent:F0}%");
                    }
                });

            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, AppPaths.FfmpegDirectory, progress);

            EnsureExecutableBits(GetToolPath(AppPaths.FfmpegDirectory, "ffmpeg"));
            EnsureExecutableBits(GetToolPath(AppPaths.FfmpegDirectory, "ffprobe"));

            if (TryConfigureExisting(out var downloadedDirectory))
            {
                return downloadedDirectory;
            }

            throw new FileNotFoundException($"FFmpeg was downloaded, but ffmpeg/ffprobe were not found in '{AppPaths.FfmpegDirectory}'.");
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    /// <summary>
    /// Turns an OS error from starting FFmpeg into an actionable message.
    /// </summary>
    public static string DescribeStartFailure(string ffmpegPath, Exception ex)
    {
        if (OperatingSystem.IsMacOS())
        {
            var isAppleSilicon = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var hint = isAppleSilicon
                ? "The automatically downloaded FFmpeg is an Intel build. Install Rosetta (softwareupdate --install-rosetta) or install a native FFmpeg with Homebrew (brew install ffmpeg), then restart the app."
                : "Install FFmpeg with Homebrew (brew install ffmpeg), then restart the app.";

            return $"Could not start FFmpeg at '{ffmpegPath}' ({ex.Message}). {hint}";
        }

        return $"Could not start FFmpeg at '{ffmpegPath}' ({ex.Message}).";
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static void Configure(string directory)
    {
        Xabe.FFmpeg.FFmpeg.SetExecutablesPath(directory, GetToolFileName("ffmpeg"), GetToolFileName("ffprobe"));
    }

    private static bool DirectoryHasFfmpeg(string directory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(directory)
                   && File.Exists(GetToolPath(directory, "ffmpeg"))
                   && File.Exists(GetToolPath(directory, "ffprobe"));
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetBundledDirectories()
    {
        yield return AppPaths.FfmpegDirectory;

        // Earlier Windows builds downloaded FFmpeg next to the executable; reuse it instead of
        // downloading another copy.
        yield return AppPaths.LegacyFfmpegDirectory;
    }

    private static IEnumerable<string> GetCandidateToolDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return directory.Trim('"');
        }

        // Apps launched from Finder or the Dock do not inherit the shell PATH.
        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/opt/local/bin";
            yield return "/usr/bin";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/local/bin";
            yield return "/usr/bin";
            yield return "/bin";
        }
    }

    private static string GetToolPath(string directory, string name)
    {
        return Path.Combine(directory, GetToolFileName(name));
    }

    private static string GetToolFileName(string name)
    {
        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }

    private static void EnsureExecutableBits(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Some filesystems do not support Unix modes; the downloaded archive usually preserves them.
        }
    }
}
