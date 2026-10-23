using System;
using System.Diagnostics;
using System.IO;

namespace PremiumYoutubeDownloader.Services;

/// <summary>
/// Minimal file log in the user data folder so crashes and download failures can be diagnosed
/// on machines without a debugger attached.
/// </summary>
public static class AppLog
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogFile => Path.Combine(AppPaths.LogDirectory, "app.log");

    public static void Error(string context, Exception exception)
    {
        Write($"ERROR {context}: {exception}");
    }

    public static void Warn(string message)
    {
        Write($"WARN  {message}");
    }

    private static void Write(string message)
    {
        Trace.WriteLine(message);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);

                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > MaxLogBytes)
                {
                    File.Move(LogFile, LogFile + ".old", overwrite: true);
                }

                File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
