using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PremiumYoutubeDownloader.Services;

public static class AppPaths
{
    private const string AppFolderName = "PremiumYoutubeDownloader";

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string UserDataDirectory
    {
        get
        {
            string root;

            if (OperatingSystem.IsMacOS())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = string.IsNullOrWhiteSpace(home)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Path.Combine(home, "Library", "Application Support");
            }
            else if (OperatingSystem.IsWindows())
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            else
            {
                var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = !string.IsNullOrWhiteSpace(xdgDataHome)
                    ? xdgDataHome
                    : Path.Combine(home, ".local", "share");
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }

            return Path.Combine(root, AppFolderName);
        }
    }

    // Settings, queue state and FFmpeg live in the per-user data folder on every platform.
    // Older Windows builds kept them next to the executable, which fails when the app is
    // installed somewhere read-only such as Program Files, so those files are only read as a fallback.
    public static string SettingsFile => Path.Combine(UserDataDirectory, "settings.json");

    public static string QueueStateFile => Path.Combine(UserDataDirectory, "download-queue.json");

    public static string FfmpegDirectory => Path.Combine(UserDataDirectory, "ffmpeg");

    public static string LogDirectory => Path.Combine(UserDataDirectory, "logs");

    public static string LegacySettingsFile => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static string LegacyQueueStateFile => Path.Combine(AppContext.BaseDirectory, "download-queue.json");

    public static string LegacyFfmpegDirectory => AppContext.BaseDirectory;

    public static string DefaultDownloadDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows() && TryGetWindowsDownloadsFolder(out var windowsDownloads))
            {
                return windowsDownloads;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var downloads = Path.Combine(home, "Downloads");
                if (Directory.Exists(downloads))
                {
                    return downloads;
                }
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return string.IsNullOrWhiteSpace(desktop) ? home : desktop;
        }
    }

    /// <summary>
    /// Writes a file through a temporary sibling so an interrupted write (crash, forced quit)
    /// never leaves a truncated settings or queue file behind.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool TryGetWindowsDownloadsFolder(out string path)
    {
        path = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var pathPointer) != 0)
            {
                return false;
            }

            try
            {
                path = Marshal.PtrToStringUni(pathPointer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }

            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
