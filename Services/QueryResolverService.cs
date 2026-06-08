using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PremiumYoutubeDownloader.Models;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.Services;

public class QueryResolverService
{
    private readonly YoutubeClient _youtube;

    public QueryResolverService()
    {
        _youtube = new YoutubeClient();
    }

    public async Task<QueryResult> ResolveAsync(string query)
    {
        bool isUrl = Uri.TryCreate(query, UriKind.Absolute, out var uriResult) 
                     && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

        if (isUrl)
        {
            // Try parsing as Playlist
            var playlistId = PlaylistId.TryParse(query);
            if (playlistId != null)
            {
                var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
                var videos = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();
                return new QueryResult
                {
                    Title = playlist.Title,
                    Videos = videos,
                    IsPlaylist = true
                };
            }

            // Try parsing as single Video
            var videoId = VideoId.TryParse(query);
            if (videoId != null)
            {
                var video = await _youtube.Videos.GetAsync(videoId.Value);
                return new QueryResult
                {
                    Title = video.Title,
                    Videos = new List<IVideo> { video },
                    IsPlaylist = false
                };
            }
        }

        // Fallback to YouTube Search
        var searchResults = await _youtube.Search.GetVideosAsync(query).CollectAsync(20);
        if (searchResults.Count > 0)
        {
            return new QueryResult
            {
                Title = $"Search Results: {query}",
                Videos = searchResults,
                IsPlaylist = true // Treat search results like a playlist grid
            };
        }

        throw new Exception("Could not find any videos for that search query or URL.");
    }
}
