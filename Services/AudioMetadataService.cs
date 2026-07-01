using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PremiumYoutubeDownloader.Models;
using SkiaSharp;

namespace PremiumYoutubeDownloader.Services;

public sealed class AudioMetadataService
{
    private const int MinCoverSize = 600;
    private const int MaxCoverSize = 1400;
    private const int JpegQuality = 90;

    private static readonly HttpClient HttpClient = new();

    public async Task ApplyYoutubeAudioMetadataAsync(
        string mp3Path,
        AudioMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mp3Path) || !File.Exists(mp3Path))
        {
            return;
        }

        var artworkSource = !string.IsNullOrWhiteSpace(metadata.ArtworkFilePath)
            ? metadata.ArtworkFilePath
            : metadata.ArtworkUrl;

        var coverArtBytes = !string.IsNullOrWhiteSpace(artworkSource)
            ? await TryCreateCompatibleCoverArtAsync(artworkSource, cancellationToken)
            : null;

        await Task.Run(() =>
        {
            var previousVersion = TagLib.Id3v2.Tag.DefaultVersion;
            var previousForceVersion = TagLib.Id3v2.Tag.ForceDefaultVersion;
            var previousEncoding = TagLib.Id3v2.Tag.DefaultEncoding;
            var previousForceEncoding = TagLib.Id3v2.Tag.ForceDefaultEncoding;

            try
            {
                TagLib.Id3v2.Tag.DefaultVersion = 3;
                TagLib.Id3v2.Tag.ForceDefaultVersion = true;
                TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.UTF16;
                TagLib.Id3v2.Tag.ForceDefaultEncoding = true;

                using var file = TagLib.File.Create(mp3Path);
                file.RemoveTags(TagLib.TagTypes.AllTags);

                var tag = file.GetTag(TagLib.TagTypes.Id3v2, true);
                var performers = SplitTagList(metadata.Artist);
                tag.Title = CleanRequiredTagValue(metadata.Title);
                tag.Performers = performers.Length > 0
                    ? performers
                    : new[] { "Unknown" };
                tag.Album = CleanOptionalTagValue(metadata.Album);
                tag.AlbumArtists = SplitTagList(metadata.AlbumArtist).DefaultIfEmpty(tag.Performers[0]).ToArray();
                tag.Genres = SplitTagList(metadata.Genre);
                tag.Year = ParsePositiveUInt(metadata.Year);
                tag.Track = ParsePositiveUInt(metadata.TrackNumber);
                tag.Disc = ParsePositiveUInt(metadata.DiscNumber);
                tag.Comment = BuildComment(metadata);

                if (coverArtBytes is { Length: > 0 })
                {
                    tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture(new TagLib.ByteVector(coverArtBytes))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = "image/jpeg",
                            Description = "Cover"
                        }
                    };
                }

                file.Save();
            }
            finally
            {
                TagLib.Id3v2.Tag.DefaultVersion = previousVersion;
                TagLib.Id3v2.Tag.ForceDefaultVersion = previousForceVersion;
                TagLib.Id3v2.Tag.DefaultEncoding = previousEncoding;
                TagLib.Id3v2.Tag.ForceDefaultEncoding = previousForceEncoding;
            }
        }, cancellationToken);

        VerifySavedMetadata(mp3Path, coverArtBytes != null);
    }

    public Task ApplyYoutubeAudioMetadataAsync(
        string mp3Path,
        string title,
        string artist,
        string? thumbnailUrl,
        string? sourceUrl,
        CancellationToken cancellationToken = default)
    {
        return ApplyYoutubeAudioMetadataAsync(
            mp3Path,
            new AudioMetadata
            {
                Title = title,
                Artist = artist,
                AlbumArtist = artist,
                SourceUrl = sourceUrl ?? string.Empty,
                ArtworkUrl = thumbnailUrl ?? string.Empty
            },
            cancellationToken);
    }

    private static async Task<byte[]?> TryCreateCompatibleCoverArtAsync(string artworkSource, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(artworkSource))
            {
                var localBytes = await File.ReadAllBytesAsync(artworkSource, cancellationToken);
                return CreateSquareJpegCover(localBytes);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, artworkSource);
            request.Headers.UserAgent.ParseAdd("PremiumYoutubeDownloader/1.0");

            var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var originalBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return CreateSquareJpegCover(originalBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? CreateSquareJpegCover(byte[] imageBytes)
    {
        using var sourceBitmap = SKBitmap.Decode(imageBytes);
        if (sourceBitmap == null || sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
        {
            return null;
        }

        var coverSize = Math.Clamp(Math.Max(sourceBitmap.Width, sourceBitmap.Height), MinCoverSize, MaxCoverSize);
        var imageInfo = new SKImageInfo(coverSize, coverSize, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var sourceImage = SKImage.FromBitmap(sourceBitmap);
        using var surface = SKSurface.Create(imageInfo);
        var canvas = surface.Canvas;

        canvas.Clear(new SKColor(18, 18, 20));

        using (var backgroundPaint = new SKPaint
        {
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(22, 22),
            Color = SKColors.White.WithAlpha(190)
        })
        {
            canvas.DrawImage(sourceImage, GetAspectFillRect(sourceBitmap.Width, sourceBitmap.Height, coverSize), backgroundPaint);
        }

        using (var dimPaint = new SKPaint { Color = SKColors.Black.WithAlpha(88) })
        {
            canvas.DrawRect(SKRect.Create(0, 0, coverSize, coverSize), dimPaint);
        }

        var foregroundRect = GetAspectFitRect(sourceBitmap.Width, sourceBitmap.Height, coverSize);
        using (var foregroundPaint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawImage(sourceImage, foregroundRect, foregroundPaint);
        }

        using var finalImage = surface.Snapshot();
        using var encoded = finalImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return encoded?.ToArray();
    }

    private static SKRect GetAspectFillRect(int sourceWidth, int sourceHeight, int destinationSize)
    {
        var scale = Math.Max(destinationSize / (float)sourceWidth, destinationSize / (float)sourceHeight);
        return GetCenteredRect(sourceWidth, sourceHeight, scale, destinationSize);
    }

    private static SKRect GetAspectFitRect(int sourceWidth, int sourceHeight, int destinationSize)
    {
        var scale = Math.Min(destinationSize / (float)sourceWidth, destinationSize / (float)sourceHeight);
        return GetCenteredRect(sourceWidth, sourceHeight, scale, destinationSize);
    }

    private static SKRect GetCenteredRect(int sourceWidth, int sourceHeight, float scale, int destinationSize)
    {
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var left = (destinationSize - width) / 2f;
        var top = (destinationSize - height) / 2f;

        return SKRect.Create(left, top, width, height);
    }

    private static string CleanRequiredTagValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    private static string? CleanOptionalTagValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string[] SplitTagList(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();
    }

    private static uint ParsePositiveUInt(string value)
    {
        return uint.TryParse(value?.Trim(), out var parsed) ? parsed : 0;
    }

    private static string? BuildComment(AudioMetadata metadata)
    {
        var comment = CleanOptionalTagValue(metadata.Comment);
        var sourceUrl = CleanOptionalTagValue(metadata.SourceUrl);

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return comment;
        }

        return string.IsNullOrWhiteSpace(comment)
            ? $"Source: {sourceUrl}"
            : $"{comment}{Environment.NewLine}Source: {sourceUrl}";
    }

    private static void VerifySavedMetadata(string mp3Path, bool expectedCoverArt)
    {
        using var file = TagLib.File.Create(mp3Path);

        if (string.IsNullOrWhiteSpace(file.Tag.Title) || file.Tag.Performers.Length == 0)
        {
            throw new InvalidOperationException("MP3 metadata was saved, but title or artist could not be read back.");
        }

        if (expectedCoverArt && file.Tag.Pictures.Length == 0)
        {
            throw new InvalidOperationException("MP3 artwork was saved, but could not be read back.");
        }
    }
}
