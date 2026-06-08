using YoutubeExplode.Videos.Streams;

namespace PremiumYoutubeDownloader.Models;

public class StreamQualityOption
{
    public string DisplayName { get; set; } = string.Empty;
    public IStreamInfo? VideoStream { get; set; }
    public IStreamInfo? AudioStream { get; set; }
    public bool RequiresFfmpeg { get; set; }

    public override string ToString() => DisplayName;
}
