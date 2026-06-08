using System.Collections.Generic;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.Models;

public class QueryResult
{
    public string Title { get; set; } = string.Empty;
    public IReadOnlyList<IVideo> Videos { get; set; } = new List<IVideo>();
    public bool IsPlaylist { get; set; }
}
