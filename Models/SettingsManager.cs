using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PremiumYoutubeDownloader.Services;

namespace PremiumYoutubeDownloader.Models;

public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private string _downloadPath = AppPaths.DefaultDownloadDirectory;

    [ObservableProperty]
    private int _concurrentDownloads = SettingsManager.DefaultConcurrentDownloads;

    [ObservableProperty]
    private string _themeMode = "Light";

    [ObservableProperty]
    private bool _autoTagAudio = true;

    [ObservableProperty]
    private bool _downloadSubtitles = true;

    [ObservableProperty]
    private bool _embedSubtitles;

    [ObservableProperty]
    private string _subtitleLanguage = "en";
}

public static class SettingsManager
{
    public const int DefaultConcurrentDownloads = 3;
    public const int MinConcurrentDownloads = 1;
    public const int MaxConcurrentDownloads = 6;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings LoadSettings()
    {
        foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.LegacySettingsFile })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                return NormalizeSettings(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Could not read settings from '{path}': {ex.Message}");
            }
        }

        return NormalizeSettings(new AppSettings());
    }

    public static bool SaveSettings(AppSettings settings)
    {
        try
        {
            AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not save settings: {ex.Message}");
            return false;
        }
    }

    private static AppSettings NormalizeSettings(AppSettings settings)
    {
        settings.ConcurrentDownloads = Math.Clamp(settings.ConcurrentDownloads, MinConcurrentDownloads, MaxConcurrentDownloads);

        if (string.IsNullOrWhiteSpace(settings.DownloadPath))
        {
            settings.DownloadPath = AppPaths.DefaultDownloadDirectory;
        }

        if (string.IsNullOrWhiteSpace(settings.SubtitleLanguage))
        {
            settings.SubtitleLanguage = "en";
        }

        // Only the light appearance is styled today.
        settings.ThemeMode = "Light";

        return settings;
    }
}
