using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;
using PremiumYoutubeDownloader.Models;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class SelectableVideoViewModel : ViewModelBase
{
    public IVideo Video { get; }
    public QueryResultKind SourceKind { get; }
    public string CollectionTitle { get; }
    public int CollectionIndex { get; }
    public AudioMetadataViewModel Metadata { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Title => Video.Title;
    public string Author => Video.Author.ChannelTitle;
    public string DurationLabel => Video.Duration?.ToString(@"hh\:mm\:ss") ?? "Live";
    public string ThumbnailUrl => Video.Thumbnails.Count > 0
        ? Video.Thumbnails.OrderByDescending(t => t.Resolution.Area).First().Url
        : string.Empty;
    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);
    public string SourceKindLabel => SourceKind.ToString();

    public SelectableVideoViewModel(
        IVideo video,
        QueryResultKind sourceKind = QueryResultKind.Video,
        string collectionTitle = "",
        int collectionIndex = 0)
    {
        Video = video;
        SourceKind = sourceKind;
        CollectionTitle = collectionTitle;
        CollectionIndex = collectionIndex;
        IsSelected = false;
        Metadata = CreateDefaultMetadata(video, sourceKind, collectionTitle, collectionIndex);
    }

    private static AudioMetadataViewModel CreateDefaultMetadata(
        IVideo video,
        QueryResultKind sourceKind,
        string collectionTitle,
        int collectionIndex)
    {
        var thumbnailUrl = video.Thumbnails.Count > 0
            ? video.Thumbnails.OrderByDescending(t => t.Resolution.Area).First().Url
            : string.Empty;

        var isAlbumLikeSource = sourceKind is QueryResultKind.Playlist or QueryResultKind.Channel;

        return AudioMetadataViewModel.FromModel(new AudioMetadata
        {
            Title = video.Title,
            Artist = video.Author.ChannelTitle,
            Album = isAlbumLikeSource ? collectionTitle : string.Empty,
            AlbumArtist = isAlbumLikeSource ? video.Author.ChannelTitle : string.Empty,
            TrackNumber = isAlbumLikeSource && collectionIndex > 0 ? collectionIndex.ToString() : string.Empty,
            SourceUrl = video.Url,
            ArtworkUrl = thumbnailUrl
        });
    }
}
