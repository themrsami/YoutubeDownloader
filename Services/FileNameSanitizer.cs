using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PremiumYoutubeDownloader.Services;

/// <summary>
/// Produces file and folder names that are valid on Windows, macOS and Linux alike, so a file
/// downloaded on one platform can still be copied to another (or to an exFAT/FAT32 drive).
/// </summary>
public static class FileNameSanitizer
{
    // Leaves room for "NNN - " prefixes, " (N)" suffixes, extensions and temporary suffixes
    // such as ".source.<guid>.webm" while staying well below the 255 byte/char limits and
    // the classic 260 character Windows path limit for typical download folders.
    public const int DefaultMaxLength = 120;

    private static readonly char[] InvalidChars =
        "<>:\"/\\|?*".ToCharArray()
            .Concat(Enumerable.Range(0, 32).Select(i => (char)i))
            .ToArray();

    private static readonly string[] ReservedWindowsNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Sanitize(string? value, int maxLength = DefaultMaxLength, string fallback = "Untitled")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasReplacement = false;

        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            if (InvalidChars.Contains(character) || char.IsControl(character))
            {
                if (!lastWasReplacement)
                {
                    builder.Append('_');
                    lastWasReplacement = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasReplacement = false;
        }

        var cleaned = CollapseWhitespace(builder.ToString()).Trim().Trim('_').Trim();

        if (cleaned.Length > maxLength)
        {
            cleaned = TruncateOnTextElement(cleaned, maxLength).Trim();
        }

        // Windows silently drops trailing dots and spaces, which would make paths mismatch.
        cleaned = cleaned.TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        var stem = cleaned.Split('.')[0].Trim();
        if (ReservedWindowsNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasWhitespace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                }

                lastWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            lastWasWhitespace = false;
        }

        return builder.ToString();
    }

    private static string TruncateOnTextElement(string value, int maxLength)
    {
        // Avoid splitting emoji or other surrogate pairs in half.
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var builder = new StringBuilder(maxLength);

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (builder.Length + element.Length > maxLength)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString();
    }
}
