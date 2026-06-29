using System;
using System.IO;
using System.Text.Json;
using PremiumYoutubeDownloader.Services;

namespace PremiumYoutubeDownloader.Models;

public class AppSettings
{
    public string DownloadPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public int ConcurrentDownloads { get; set; } = SettingsManager.DefaultConcurrentDownloads;
    public string ThemeMode { get; set; } = "Dark"; // "Dark" or "Light"
    public bool AutoTagAudio { get; set; } = true;
    public bool DownloadSubtitles { get; set; } = true;
    public bool EmbedSubtitles { get; set; } = false;
    public string SubtitleLanguage { get; set; } = "en";
}

public static class SettingsManager
{
    public const int DefaultConcurrentDownloads = 3;

    private static readonly string SettingsFile = AppPaths.SettingsFile;
    private static readonly string LegacySettingsFile = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings LoadSettings()
    {
        var path = File.Exists(SettingsFile) ? SettingsFile : LegacySettingsFile;

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return NormalizeSettings(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
            }
            catch { }
        }

        return NormalizeSettings(new AppSettings());
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    private static AppSettings NormalizeSettings(AppSettings settings)
    {
        if (settings.ConcurrentDownloads < DefaultConcurrentDownloads)
        {
            settings.ConcurrentDownloads = DefaultConcurrentDownloads;
        }

        if (string.IsNullOrWhiteSpace(settings.DownloadPath))
        {
            settings.DownloadPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        return settings;
    }
}
