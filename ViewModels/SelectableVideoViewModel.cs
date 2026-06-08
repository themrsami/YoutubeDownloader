using CommunityToolkit.Mvvm.ComponentModel;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class SelectableVideoViewModel : ViewModelBase
{
    public IVideo Video { get; }

    [ObservableProperty]
    private bool _isSelected;

    public SelectableVideoViewModel(IVideo video)
    {
        Video = video;
        IsSelected = false;
    }
}
