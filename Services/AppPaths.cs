using System;
using System.IO;

namespace PremiumYoutubeDownloader.Services;

public static class AppPaths
{
    private const string AppFolderName = "PremiumYoutubeDownloader";

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

    public static string SettingsFile => Path.Combine(GetSettingsDirectory(), "settings.json");

    public static string QueueStateFile => Path.Combine(GetSettingsDirectory(), "download-queue.json");

    public static string FfmpegDirectory => OperatingSystem.IsWindows()
        ? AppContext.BaseDirectory
        : Path.Combine(UserDataDirectory, "ffmpeg");

    private static string GetSettingsDirectory()
    {
        return OperatingSystem.IsWindows()
            ? AppContext.BaseDirectory
            : UserDataDirectory;
    }
}
