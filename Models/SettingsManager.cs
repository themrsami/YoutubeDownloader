using System;
using System.IO;
using System.Text.Json;

namespace PremiumYoutubeDownloader.Models;

public class AppSettings
{
    public string DownloadPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public int ConcurrentDownloads { get; set; } = 3;
    public string ThemeMode { get; set; } = "Dark"; // "Dark" or "Light"
    public bool AutoTagAudio { get; set; } = true;
    public bool DownloadSubtitles { get; set; } = true;
    public bool EmbedSubtitles { get; set; } = false;
    public string SubtitleLanguage { get; set; } = "en";
}

public static class SettingsManager
{
    private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static AppSettings LoadSettings()
    {
        if (File.Exists(SettingsFile))
        {
            try
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch { }
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}
