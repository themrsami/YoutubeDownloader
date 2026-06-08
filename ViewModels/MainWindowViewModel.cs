using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PremiumYoutubeDownloader.Models;
using PremiumYoutubeDownloader.Services;
using Avalonia.Threading;
using YoutubeExplode;
using YoutubeExplode.Videos;
using Material.Styles.Themes;
using Material.Colors;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly QueryResolverService _queryResolver;
    private readonly VideoDownloaderService _videoDownloader;
    private readonly YoutubeClient _youtube;

    [ObservableProperty]
    private bool _isInitializing;

    [ObservableProperty]
    private string _initializationMessage = "Initializing...";

    public MainWindowViewModel()
    {
        _youtube = new YoutubeClient();
        _queryResolver = new QueryResolverService();
        _videoDownloader = new VideoDownloaderService();
        Videos = new ObservableCollection<SelectableVideoViewModel>();
        FilteredVideos = new ObservableCollection<SelectableVideoViewModel>();
        AvailableQualities = new ObservableCollection<StreamQualityOption>();
        DownloadQueue = new ObservableCollection<DownloadTaskViewModel>();
        AppSettings = SettingsManager.LoadSettings();
        Dispatcher.UIThread.Post(() => ApplyTheme(AppSettings.ThemeMode), Avalonia.Threading.DispatcherPriority.Loaded);
        
        _ = InitializeAppAsync();
    }

    private async Task InitializeAppAsync()
    {
        try
        {
            var ffmpegPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            var ffprobePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffprobe.exe");

            if (!System.IO.File.Exists(ffmpegPath) || !System.IO.File.Exists(ffprobePath))
            {
                IsInitializing = true;
                InitializationMessage = "Downloading Required Components (First Launch Only). Please wait...";
                await Xabe.FFmpeg.Downloader.FFmpegDownloader.GetLatestVersion(Xabe.FFmpeg.Downloader.FFmpegVersion.Official, AppDomain.CurrentDomain.BaseDirectory);
                IsInitializing = false;
            }
        }
        catch (Exception ex)
        {
            InitializationMessage = "Error downloading components: " + ex.Message;
            await Task.Delay(5000);
            IsInitializing = false;
        }
    }

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _playlistSearchQuery = string.Empty;

    partial void OnPlaylistSearchQueryChanged(string value)
    {
        FilteredVideos.Clear();
        foreach (var v in Videos)
        {
            if (string.IsNullOrWhiteSpace(value) || v.Video.Title.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                FilteredVideos.Add(v);
            }
        }
    }

    [ObservableProperty]
    private bool _isAllSelected;

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var video in FilteredVideos)
        {
            video.IsSelected = value;
        }
    }

    public ObservableCollection<SelectableVideoViewModel> Videos { get; }
    public ObservableCollection<SelectableVideoViewModel> FilteredVideos { get; }
    
    public ObservableCollection<StreamQualityOption> AvailableQualities { get; }
    
    public ObservableCollection<DownloadTaskViewModel> DownloadQueue { get; }

    public ObservableCollection<string> GlobalQualities { get; } = new ObservableCollection<string>
    {
        "Highest Video (Native)",
        "Highest Audio Only",
        "1080p Video (Requires FFmpeg)",
        "720p Video (Requires FFmpeg)",
        "Lowest Audio Only"
    };

    public ObservableCollection<string> ThemeModes { get; } = new ObservableCollection<string> { "Dark", "Light" };

    [ObservableProperty]
    private string _selectedGlobalQuality = "Highest Video (Native)";

    [ObservableProperty]
    private string _queueTabHeader = "Queue";

    private void UpdateQueueHeader()
    {
        var activeCount = DownloadQueue.Count(q => !q.IsCompleted && !q.HasError);
        QueueTabHeader = activeCount > 0 ? $"Queue ({activeCount})" : "Queue";
    }

    [ObservableProperty]
    private AppSettings _appSettings;

    [ObservableProperty]
    private SelectableVideoViewModel? _selectedVideo;

    [ObservableProperty]
    private StreamQualityOption? _selectedQuality;

    [ObservableProperty]
    private string _videoTitle = string.Empty;

    [ObservableProperty]
    private string _videoAuthor = string.Empty;

    [ObservableProperty]
    private string _videoDuration = string.Empty;

    [ObservableProperty]
    private string _thumbnailUrl = string.Empty;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private bool _isVideoLoaded = false;

    [ObservableProperty]
    private bool _isPlaylistLoaded = false;

    [ObservableProperty]
    private bool _isLoadingQualities = false;

    [ObservableProperty]
    private string _qualitiesPlaceholder = "Select Quality...";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [RelayCommand]
    private void SaveSettings()
    {
        SettingsManager.SaveSettings(AppSettings);
        ApplyTheme(AppSettings.ThemeMode);
        StatusMessage = "Settings Saved successfully!";
    }

    private void ApplyTheme(string themeMode)
    {
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = themeMode == "Light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
            
            var materialTheme = Avalonia.Application.Current.Styles.OfType<MaterialTheme>().FirstOrDefault();
            if (materialTheme != null)
            {
                var baseTheme = themeMode == "Light" ? Material.Styles.Themes.Base.BaseThemeMode.Light.GetBaseTheme() : Material.Styles.Themes.Base.BaseThemeMode.Dark.GetBaseTheme();
                materialTheme.CurrentTheme = Material.Styles.Themes.Theme.Create(baseTheme, materialTheme.CurrentTheme.PrimaryLight.Color, materialTheme.CurrentTheme.SecondaryLight.Color);
            }
        }
    }

    public Func<Task<string?>>? SelectDownloadFolderAction { get; set; }

    [RelayCommand]
    private async Task SelectDownloadFolder()
    {
        if (SelectDownloadFolderAction != null)
        {
            var path = await SelectDownloadFolderAction();
            if (!string.IsNullOrEmpty(path))
            {
                // Re-instantiate to trigger the ObservableProperty UI refresh instantly
                AppSettings = new AppSettings
                {
                    DownloadPath = path,
                    ConcurrentDownloads = AppSettings.ConcurrentDownloads,
                    ThemeMode = AppSettings.ThemeMode,
                    AutoTagAudio = AppSettings.AutoTagAudio,
                    DownloadSubtitles = AppSettings.DownloadSubtitles
                };
                StatusMessage = "Download path updated.";
            }
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsLoading = true;
        IsVideoLoaded = false;
        IsPlaylistLoaded = false;
        Videos.Clear();
        FilteredVideos.Clear();
        AvailableQualities.Clear();
        StatusMessage = "Searching YouTube...";

        try
        {
            var result = await _queryResolver.ResolveAsync(SearchQuery);
            
            if (result.IsPlaylist)
            {
                foreach (var video in result.Videos)
                {
                    var vm = new SelectableVideoViewModel(video);
                    Videos.Add(vm);
                    FilteredVideos.Add(vm);
                }
                IsPlaylistLoaded = true;
                StatusMessage = $"Playlist/Search loaded: {result.Title} ({Videos.Count} videos)";
                if (FilteredVideos.Any()) SelectedVideo = FilteredVideos.First();
            }
            else
            {
                var video = result.Videos.FirstOrDefault();
                if (video != null)
                {
                    var vm = new SelectableVideoViewModel(video);
                    Videos.Add(vm);
                    FilteredVideos.Add(vm);
                    SelectedVideo = vm;
                    StatusMessage = "Video details loaded successfully.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    async partial void OnSelectedVideoChanged(SelectableVideoViewModel? value)
    {
        if (value == null)
        {
            IsVideoLoaded = false;
            return;
        }

        var currentVideo = value.Video;

        VideoTitle = currentVideo.Title;
        VideoAuthor = currentVideo.Author.ChannelTitle;
        VideoDuration = currentVideo.Duration?.ToString(@"hh\:mm\:ss") ?? "Unknown";
        ThumbnailUrl = currentVideo.Thumbnails.Count > 0 ? currentVideo.Thumbnails.OrderByDescending(t => t.Resolution.Area).First().Url : string.Empty;
        
        IsVideoLoaded = true;
        AvailableQualities.Clear();
        SelectedQuality = null;

        try
        {
            IsLoadingQualities = true;
            QualitiesPlaceholder = "Loading formats...";
            StatusMessage = "Fetching available stream qualities...";
            
            var qualities = await _videoDownloader.GetAvailableQualitiesAsync(currentVideo.Url);
            
            if (SelectedVideo?.Video.Url != currentVideo.Url) return;

            foreach (var q in qualities)
            {
                AvailableQualities.Add(q);
            }
            if (AvailableQualities.Any())
                SelectedQuality = AvailableQualities.First();
            
            QualitiesPlaceholder = "Select Quality...";
            StatusMessage = "Qualities loaded. Ready to download.";
        }
        catch (Exception ex)
        {
            if (SelectedVideo?.Video.Url != currentVideo.Url) return;
            QualitiesPlaceholder = "Failed to load formats.";
            StatusMessage = "Could not fetch stream info: " + ex.Message;
        }
        finally
        {
            if (SelectedVideo?.Video.Url == currentVideo.Url)
                IsLoadingQualities = false;
        }
    }

    private SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(3, 3);

    partial void OnAppSettingsChanged(AppSettings value)
    {
        _downloadSemaphore = new SemaphoreSlim(value.ConcurrentDownloads, value.ConcurrentDownloads);
    }

    [RelayCommand]
    private void DownloadSelected()
    {
        var toDownload = Videos.Where(v => v.IsSelected).ToList();
        if (!toDownload.Any())
        {
            if (SelectedVideo != null) toDownload.Add(SelectedVideo);
            else return;
        }

        foreach (var vid in toDownload)
        {
            var preference = SelectedGlobalQuality;
            if (vid == SelectedVideo && SelectedQuality != null)
            {
                preference = SelectedQuality.DisplayName;
            }
            EnqueueDownload(vid.Video, preference);
        }
    }

    private void EnqueueDownload(IVideo video, string qualityPreference)
    {
        var safeTitle = string.Join("_", video.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
        var isAudioOnly = qualityPreference.Contains("Audio");
        var ext = isAudioOnly ? ".mp3" : ".mp4";
        var filePath = System.IO.Path.Combine(AppSettings.DownloadPath, $"{safeTitle}{ext}");

        var taskVm = new DownloadTaskViewModel(video.Title, null!, filePath);
        DownloadQueue.Add(taskVm);
        UpdateQueueHeader();

        _ = ProcessDownloadAsync(taskVm, video.Url, qualityPreference);
    }

    private async Task ProcessDownloadAsync(DownloadTaskViewModel taskVm, string videoUrl, string qualityPreference)
    {
        await _downloadSemaphore.WaitAsync();
        try
        {
            var qualities = await _videoDownloader.GetAvailableQualitiesAsync(videoUrl);
            StreamQualityOption? targetQuality = qualities.FirstOrDefault(q => q.DisplayName == qualityPreference);
            if (targetQuality == null)
            {
                if (qualityPreference == "Highest Audio Only") targetQuality = qualities.FirstOrDefault(q => q.DisplayName.Contains("Audio Only"));
                else if (qualityPreference == "Lowest Audio Only") targetQuality = qualities.LastOrDefault(q => q.DisplayName.Contains("Audio Only"));
                else if (qualityPreference == "1080p Video (Requires FFmpeg)") targetQuality = qualities.FirstOrDefault(q => q.DisplayName.Contains("1080p")) ?? qualities.FirstOrDefault(q => !q.DisplayName.Contains("Audio"));
                else if (qualityPreference == "720p Video (Requires FFmpeg)") targetQuality = qualities.FirstOrDefault(q => q.DisplayName.Contains("720p")) ?? qualities.FirstOrDefault(q => !q.DisplayName.Contains("Audio"));
                else targetQuality = qualities.FirstOrDefault(q => !q.RequiresFfmpeg && !q.DisplayName.Contains("Audio Only"));
            }
            if (targetQuality == null) targetQuality = qualities.FirstOrDefault();
            
            if (targetQuality == null)
            {
                taskVm.Status = "Failed: Could not find matching stream quality.";
                taskVm.HasError = true;
                return;
            }

            var totalBytes = (targetQuality.VideoStream?.Size.Bytes ?? 0) + (targetQuality.AudioStream?.Size.Bytes ?? 0);
            DateTime lastTime = DateTime.UtcNow;
            double lastProgress = 0;

            taskVm.Status = "Downloading...";
            var progress = new Progress<double>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    taskVm.Progress = p * 100;
                    var now = DateTime.UtcNow;
                    var elapsed = (now - lastTime).TotalSeconds;
                    if (elapsed >= 0.5)
                    {
                        var downloadedSinceLast = (p - lastProgress) * totalBytes;
                        var speedBps = downloadedSinceLast / elapsed;
                        var speedMbps = speedBps / 1024 / 1024;
                        taskVm.Status = $"Downloading: {taskVm.Progress:F1}% ({speedMbps:F2} MB/s)";
                        lastTime = now;
                        lastProgress = p;
                    }
                });
            });

            if (targetQuality == null)
            {
                taskVm.Status = "Failed: Could not find matching stream quality.";
                taskVm.HasError = true;
                return;
            }

            var isAudioOnly = targetQuality.DisplayName.Contains("Audio Only");
            var needsFfmpeg = targetQuality.RequiresFfmpeg || (isAudioOnly && taskVm.FilePath.EndsWith(".mp3")) || AppSettings.DownloadSubtitles;

            if (needsFfmpeg && !System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe")))
            {
                Dispatcher.UIThread.Post(() => taskVm.Status = "Downloading FFmpeg for the first time...");
                await Xabe.FFmpeg.Downloader.FFmpegDownloader.GetLatestVersion(Xabe.FFmpeg.Downloader.FFmpegVersion.Official, AppDomain.CurrentDomain.BaseDirectory);
            }
            
            if (System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe")))
            {
                Xabe.FFmpeg.FFmpeg.SetExecutablesPath(AppDomain.CurrentDomain.BaseDirectory);
            }

            await _videoDownloader.DownloadAsync(targetQuality, taskVm.FilePath, progress, taskVm.CancellationTokenSource.Token);
            
            // Post-download advanced integrations

            if (AppSettings.DownloadSubtitles && !isAudioOnly)
            {
                Dispatcher.UIThread.Post(() => taskVm.Status = "Downloading Subtitles...");
                var vttPath = System.IO.Path.ChangeExtension(taskVm.FilePath, ".vtt");
                var downloadedSub = await _videoDownloader.DownloadSubtitlesAsync(videoUrl, vttPath, AppSettings.SubtitleLanguage);

                if (downloadedSub == null)
                {
                    Dispatcher.UIThread.Post(() => taskVm.Status = "Warning: No subtitles found for this video on YouTube.");
                    await Task.Delay(2500); // Give user time to see the warning
                }
                else
                {
                    if (System.IO.File.Exists(vttPath))
                    {
                        var srtPath = System.IO.Path.ChangeExtension(taskVm.FilePath, ".srt");
                        try
                        {
                            var srtConv = Xabe.FFmpeg.FFmpeg.Conversions.New().AddParameter($"-i \"{vttPath}\" \"{srtPath}\"");
                            await srtConv.Start(taskVm.CancellationTokenSource.Token);
                            System.IO.File.Delete(vttPath);
                            downloadedSub = srtPath;
                        }
                        catch (Exception ex) 
                        { 
                            Dispatcher.UIThread.Post(() => taskVm.Status = "Subtitle error: " + ex.Message);
                            await Task.Delay(2000);
                        }
                    }

                    if (AppSettings.EmbedSubtitles && System.IO.File.Exists(taskVm.FilePath))
                    {
                        Dispatcher.UIThread.Post(() => taskVm.Status = "Embedding Subtitles into Video...");
                        try 
                        {
                            var tempPath = System.IO.Path.ChangeExtension(taskVm.FilePath, ".temp" + System.IO.Path.GetExtension(taskVm.FilePath));
                            var conversion = Xabe.FFmpeg.FFmpeg.Conversions.New()
                                .AddParameter($"-i \"{taskVm.FilePath}\" -i \"{downloadedSub}\" -map 0 -map 1 -c copy -c:s mov_text -disposition:s:0 default \"{tempPath}\"");
                            
                            await conversion.Start(taskVm.CancellationTokenSource.Token);
                            
                            if (System.IO.File.Exists(tempPath))
                            {
                                System.IO.File.Delete(taskVm.FilePath);
                                System.IO.File.Move(tempPath, taskVm.FilePath);
                                System.IO.File.Delete(downloadedSub);
                            }
                        }
                        catch (Exception ex) 
                        { 
                            Dispatcher.UIThread.Post(() => taskVm.Status = "Embedding error: " + ex.Message);
                            await Task.Delay(2000);
                        }
                    }
                }
            }

            if (isAudioOnly && AppSettings.AutoTagAudio)
            {
                Dispatcher.UIThread.Post(() => taskVm.Status = "Injecting ID3 Tags...");
                await Task.Run(async () =>
                {
                    try
                    {
                        var tfile = TagLib.File.Create(taskVm.FilePath);
                        tfile.Tag.Title = taskVm.Title;
                        var videoInfo = await _videoDownloader.GetVideoDetailsAsync(videoUrl);
                        tfile.Tag.Performers = new[] { videoInfo.Author.ChannelTitle };

                        var thumbUrl = videoInfo.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url;
                        if (thumbUrl != null)
                        {
                            using var hc = new HttpClient();
                            var imgBytes = await hc.GetByteArrayAsync(thumbUrl);
                            var pic = new TagLib.Picture(new TagLib.ByteVector(imgBytes)) { Type = TagLib.PictureType.FrontCover };
                            tfile.Tag.Pictures = new TagLib.IPicture[] { pic };
                        }
                        tfile.Save();
                    }
                    catch { /* Tagging errors skipped */ }
                });
            }

            Dispatcher.UIThread.Post(() =>
            {
                taskVm.Status = "Completed";
                taskVm.IsCompleted = true;
                taskVm.Progress = 100;
                UpdateQueueHeader();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                taskVm.HasError = true;
                taskVm.Status = "Error: " + ex.Message;
                UpdateQueueHeader();
            });
        }
        finally
        {
            _downloadSemaphore.Release();
            Dispatcher.UIThread.Post(() => UpdateQueueHeader());
        }
    }
}
