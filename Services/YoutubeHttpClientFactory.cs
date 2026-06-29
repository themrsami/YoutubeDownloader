using System;
using System.Net.Http;

namespace PremiumYoutubeDownloader.Services;

public static class YoutubeHttpClientFactory
{
    public static HttpClient Create()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }
}
