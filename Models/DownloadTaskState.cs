namespace PremiumYoutubeDownloader.Models;

public enum DownloadTaskState
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled
}
