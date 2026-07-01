using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PremiumYoutubeDownloader.Models;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace PremiumYoutubeDownloader.Services;

public class QueryResolverService
{
    private readonly YoutubeClient _youtube;

    public QueryResolverService()
    {
        _youtube = new YoutubeClient(YoutubeHttpClientFactory.Create());
    }

    public async Task<QueryResult> ResolveAsync(
        string query,
        Func<QueryVideoItem, Task>? onVideoResolved = null,
        Func<string, Task>? onStatusChanged = null,
        CancellationToken cancellationToken = default)
    {
        bool isUrl = Uri.TryCreate(query, UriKind.Absolute, out var uriResult) 
                     && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

        if (isUrl)
        {
            var videoId = VideoId.TryParse(query);

            // Try parsing as Playlist
            var playlistId = PlaylistId.TryParse(query);
            if (playlistId != null)
            {
                try
                {
                    var playlist = await _youtube.Playlists.GetAsync(playlistId.Value, cancellationToken);
                    var videos = await CollectVideosAsync(
                        _youtube.Playlists.GetVideosAsync(playlistId.Value, cancellationToken),
                        QueryResultKind.Playlist,
                        playlist.Title,
                        onVideoResolved,
                        onStatusChanged,
                        cancellationToken);

                    return new QueryResult
                    {
                        Title = playlist.Title,
                        Videos = videos,
                        Kind = QueryResultKind.Playlist
                    };
                }
                catch (Exception ex) when (videoId != null)
                {
                    var video = await _youtube.Videos.GetAsync(videoId.Value, cancellationToken);
                    return new QueryResult
                    {
                        Title = video.Title,
                        Videos = new List<IVideo> { video },
                        Kind = QueryResultKind.Video,
                        WarningMessage = GetPlaylistFallbackMessage(playlistId.Value.Value, ex)
                    };
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(GetPlaylistUnavailableMessage(playlistId.Value.Value, ex), ex);
                }
            }

            var channel = await TryResolveChannelAsync(query, uriResult!, cancellationToken);
            if (channel != null)
            {
                var videos = await CollectVideosAsync(
                    _youtube.Channels.GetUploadsAsync(channel.Id, cancellationToken),
                    QueryResultKind.Channel,
                    channel.Title,
                    onVideoResolved,
                    onStatusChanged,
                    cancellationToken);

                return new QueryResult
                {
                    Title = channel.Title,
                    Videos = videos,
                    Kind = QueryResultKind.Channel
                };
            }

            // Try parsing as single Video
            if (videoId != null)
            {
                var video = await _youtube.Videos.GetAsync(videoId.Value, cancellationToken);
                return new QueryResult
                {
                    Title = video.Title,
                    Videos = new List<IVideo> { video },
                    Kind = QueryResultKind.Video
                };
            }
        }

        if (query.TrimStart().StartsWith("@", StringComparison.Ordinal))
        {
            var handle = ChannelHandle.TryParse(query.Trim());
            if (handle != null)
            {
                var channel = await _youtube.Channels.GetByHandleAsync(handle.Value, cancellationToken);
                var videos = await CollectVideosAsync(
                    _youtube.Channels.GetUploadsAsync(channel.Id, cancellationToken),
                    QueryResultKind.Channel,
                    channel.Title,
                    onVideoResolved,
                    onStatusChanged,
                    cancellationToken);

                return new QueryResult
                {
                    Title = channel.Title,
                    Videos = videos,
                    Kind = QueryResultKind.Channel
                };
            }
        }

        // Fallback to YouTube Search
        var searchResults = await CollectVideosAsync(
            _youtube.Search.GetVideosAsync(query, cancellationToken),
            QueryResultKind.Search,
            $"Search Results: {query}",
            onVideoResolved,
            onStatusChanged,
            cancellationToken,
            maxItems: 20);

        if (searchResults.Count > 0)
        {
            return new QueryResult
            {
                Title = $"Search Results: {query}",
                Videos = searchResults,
                Kind = QueryResultKind.Search
            };
        }

        throw new Exception("Could not find any videos for that search query or URL.");
    }

    private async Task<Channel?> TryResolveChannelAsync(string query, Uri uri, CancellationToken cancellationToken)
    {
        if (!IsYoutubeHost(uri.Host))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        var first = Uri.UnescapeDataString(segments[0]);

        try
        {
            if (first.StartsWith("@", StringComparison.Ordinal))
            {
                var handle = ChannelHandle.TryParse(first);
                return handle == null
                    ? null
                    : await _youtube.Channels.GetByHandleAsync(handle.Value, cancellationToken);
            }

            if (first.Equals("channel", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
            {
                var channelId = ChannelId.TryParse(Uri.UnescapeDataString(segments[1]));
                return channelId == null
                    ? null
                    : await _youtube.Channels.GetAsync(channelId.Value, cancellationToken);
            }

            if (first.Equals("c", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
            {
                var slug = ChannelSlug.TryParse(Uri.UnescapeDataString(segments[1]));
                return slug == null
                    ? null
                    : await _youtube.Channels.GetBySlugAsync(slug.Value, cancellationToken);
            }

            if (first.Equals("user", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
            {
                var userName = UserName.TryParse(Uri.UnescapeDataString(segments[1]));
                return userName == null
                    ? null
                    : await _youtube.Channels.GetByUserAsync(userName.Value, cancellationToken);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsYoutubeHost(string host)
    {
        return host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<IVideo>> CollectVideosAsync(
        IAsyncEnumerable<IVideo> source,
        QueryResultKind kind,
        string collectionTitle,
        Func<QueryVideoItem, Task>? onVideoResolved,
        Func<string, Task>? onStatusChanged,
        CancellationToken cancellationToken,
        int? maxItems = null)
    {
        var videos = new List<IVideo>();
        var count = 0;

        if (onStatusChanged != null)
        {
            await onStatusChanged($"Loading {GetKindLabel(kind).ToLowerInvariant()} videos...");
        }

        await foreach (var video in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            videos.Add(video);

            if (onVideoResolved != null)
            {
                await onVideoResolved(new QueryVideoItem
                {
                    Video = video,
                    Kind = kind,
                    CollectionTitle = collectionTitle,
                    Index = count
                });
            }

            if (count == 1 || count % 25 == 0)
            {
                if (onStatusChanged != null)
                {
                    await onStatusChanged($"Loaded {count} {GetKindLabel(kind).ToLowerInvariant()} videos...");
                }
            }

            if (maxItems.HasValue && count >= maxItems.Value)
            {
                break;
            }
        }

        return videos;
    }

    private static string GetKindLabel(QueryResultKind kind)
    {
        return kind switch
        {
            QueryResultKind.Playlist => "Playlist",
            QueryResultKind.Channel => "Channel",
            QueryResultKind.Search => "Search",
            _ => "Video"
        };
    }

    private static string GetPlaylistFallbackMessage(string playlistId, Exception ex)
    {
        if (IsKnownPrivatePlaylist(playlistId))
        {
            return "This is a private YouTube playlist, so the app loaded the video from the link instead.";
        }

        return $"The playlist could not be loaded ({GetShortError(ex)}), so the app loaded the video from the link instead.";
    }

    private static string GetPlaylistUnavailableMessage(string playlistId, Exception ex)
    {
        if (IsKnownPrivatePlaylist(playlistId))
        {
            return "This YouTube playlist is private or account-only. Open a public playlist URL, or paste a video link from it.";
        }

        return $"Playlist could not be loaded: {GetShortError(ex)}";
    }

    private static bool IsKnownPrivatePlaylist(string playlistId)
    {
        return playlistId.Equals("WL", StringComparison.OrdinalIgnoreCase)
               || playlistId.Equals("LL", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetShortError(Exception ex)
    {
        var message = ex.Message;
        return string.IsNullOrWhiteSpace(message)
            ? ex.GetType().Name
            : message;
    }
}
