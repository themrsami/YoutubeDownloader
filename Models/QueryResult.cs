using System.Collections.Generic;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.Models;

public class QueryResult
{
    public string Title { get; set; } = string.Empty;
    public IReadOnlyList<IVideo> Videos { get; set; } = new List<IVideo>();
    public QueryResultKind Kind { get; set; } = QueryResultKind.Video;
    public string WarningMessage { get; set; } = string.Empty;
    public bool IsPlaylist
    {
        get => Kind is QueryResultKind.Playlist or QueryResultKind.Search or QueryResultKind.Channel;
        set => Kind = value ? QueryResultKind.Playlist : QueryResultKind.Video;
    }
    public bool IsCollection => Kind is QueryResultKind.Playlist or QueryResultKind.Search or QueryResultKind.Channel;
}
