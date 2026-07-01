using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.Models;

public sealed class QueryVideoItem
{
    public required IVideo Video { get; init; }
    public required QueryResultKind Kind { get; init; }
    public required string CollectionTitle { get; init; }
    public required int Index { get; init; }
}
