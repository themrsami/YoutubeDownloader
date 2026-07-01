using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PremiumYoutubeDownloader.Models;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class AudioMetadataViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private string _albumArtist = string.Empty;

    [ObservableProperty]
    private string _genre = string.Empty;

    [ObservableProperty]
    private string _year = string.Empty;

    [ObservableProperty]
    private string _trackNumber = string.Empty;

    [ObservableProperty]
    private string _discNumber = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    private string _sourceUrl = string.Empty;

    [ObservableProperty]
    private string _artworkUrl = string.Empty;

    [ObservableProperty]
    private string _artworkFilePath = string.Empty;

    public bool HasLocalArtwork => !string.IsNullOrWhiteSpace(ArtworkFilePath);

    public string ArtworkSourceSummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ArtworkFilePath))
            {
                return "Local image: " + Path.GetFileName(ArtworkFilePath);
            }

            return string.IsNullOrWhiteSpace(ArtworkUrl)
                ? "No artwork selected"
                : "YouTube thumbnail";
        }
    }

    public static AudioMetadataViewModel FromModel(AudioMetadata metadata)
    {
        return new AudioMetadataViewModel
        {
            Title = metadata.Title,
            Artist = metadata.Artist,
            Album = metadata.Album,
            AlbumArtist = metadata.AlbumArtist,
            Genre = metadata.Genre,
            Year = metadata.Year,
            TrackNumber = metadata.TrackNumber,
            DiscNumber = metadata.DiscNumber,
            Comment = metadata.Comment,
            SourceUrl = metadata.SourceUrl,
            ArtworkUrl = metadata.ArtworkUrl,
            ArtworkFilePath = metadata.ArtworkFilePath
        };
    }

    public AudioMetadata ToModel()
    {
        return new AudioMetadata
        {
            Title = Title,
            Artist = Artist,
            Album = Album,
            AlbumArtist = AlbumArtist,
            Genre = Genre,
            Year = Year,
            TrackNumber = TrackNumber,
            DiscNumber = DiscNumber,
            Comment = Comment,
            SourceUrl = SourceUrl,
            ArtworkUrl = ArtworkUrl,
            ArtworkFilePath = ArtworkFilePath
        };
    }

    public AudioMetadataViewModel Clone()
    {
        return FromModel(ToModel());
    }

    partial void OnArtworkFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasLocalArtwork));
        OnPropertyChanged(nameof(ArtworkSourceSummary));
    }

    partial void OnArtworkUrlChanged(string value)
    {
        OnPropertyChanged(nameof(ArtworkSourceSummary));
    }
}
