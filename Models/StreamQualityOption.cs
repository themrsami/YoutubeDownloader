using YoutubeExplode.Videos.Streams;

namespace PremiumYoutubeDownloader.Models;

public class StreamQualityOption
{
    public string DisplayName { get; set; } = string.Empty;
    public IStreamInfo? VideoStream { get; set; }
    public IStreamInfo? AudioStream { get; set; }
    public bool RequiresFfmpeg { get; set; }

    /// <summary>
    /// File extension (with the leading dot) that matches the selected streams, e.g. ".mp4".
    /// </summary>
    public string OutputExtension { get; set; } = ".mp4";

    public long TotalBytes =>
        (VideoStream?.Size.Bytes ?? 0)
        + (AudioStream != null && !ReferenceEquals(AudioStream, VideoStream) ? AudioStream.Size.Bytes : 0);

    public override string ToString() => DisplayName;
}
