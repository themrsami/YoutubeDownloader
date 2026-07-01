namespace PremiumYoutubeDownloader.Models;

public sealed class AudioMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string TrackNumber { get; set; } = string.Empty;
    public string DiscNumber { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string ArtworkUrl { get; set; } = string.Empty;
    public string ArtworkFilePath { get; set; } = string.Empty;

    public AudioMetadata Clone()
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
}
