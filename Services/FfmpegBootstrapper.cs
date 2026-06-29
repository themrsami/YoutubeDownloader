using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg.Downloader;

namespace PremiumYoutubeDownloader.Services;

public static class FfmpegBootstrapper
{
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);

    public static bool TryConfigureExisting(out string directory)
    {
        if (DirectoryHasFfmpeg(AppPaths.FfmpegDirectory))
        {
            Configure(AppPaths.FfmpegDirectory);
            directory = AppPaths.FfmpegDirectory;
            return true;
        }

        var systemDirectory = FindSystemFfmpegDirectory();
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            Configure(systemDirectory);
            directory = systemDirectory;
            return true;
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

            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, AppPaths.FfmpegDirectory);

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

    private static void Configure(string directory)
    {
        Xabe.FFmpeg.FFmpeg.SetExecutablesPath(directory, GetToolFileName("ffmpeg"), GetToolFileName("ffprobe"));
    }

    private static bool DirectoryHasFfmpeg(string directory)
    {
        return !string.IsNullOrWhiteSpace(directory)
               && File.Exists(GetToolPath(directory, "ffmpeg"))
               && File.Exists(GetToolPath(directory, "ffprobe"));
    }

    private static string? FindSystemFfmpegDirectory()
    {
        foreach (var directory in GetCandidateToolDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DirectoryHasFfmpeg(directory))
            {
                return directory;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateToolDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
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
