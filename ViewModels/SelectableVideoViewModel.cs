using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class SelectableVideoViewModel : ViewModelBase
{
    public IVideo Video { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Title => Video.Title;
    public string Author => Video.Author.ChannelTitle;
    public string DurationLabel => Video.Duration?.ToString(@"hh\:mm\:ss") ?? "Live";
    public string ThumbnailUrl => Video.Thumbnails.Count > 0
        ? Video.Thumbnails.OrderByDescending(t => t.Resolution.Area).First().Url
        : string.Empty;
    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    public SelectableVideoViewModel(IVideo video)
    {
        Video = video;
        IsSelected = false;
    }
}
