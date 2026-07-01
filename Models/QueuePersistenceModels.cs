using System.Collections.Generic;

namespace PremiumYoutubeDownloader.Models;

public sealed class DownloadQueueSnapshot
{
    public int Version { get; set; } = 1;
    public List<DownloadQueueItemSnapshot> Items { get; set; } = new();
}

public sealed class DownloadQueueItemSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string QualityLabel { get; set; } = string.Empty;
    public string VideoUrl { get; set; } = string.Empty;
    public DownloadTaskState State { get; set; } = DownloadTaskState.Pending;
    public double Progress { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsAudio { get; set; }
    public QueryResultKind SourceKind { get; set; } = QueryResultKind.Video;
    public string CollectionTitle { get; set; } = string.Empty;
    public int CollectionIndex { get; set; }
    public AudioMetadata Metadata { get; set; } = new();
}
