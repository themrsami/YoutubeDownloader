using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.Http;
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
    private readonly AudioMetadataService _audioMetadataService;
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
        _audioMetadataService = new AudioMetadataService();
        Videos = new ObservableCollection<SelectableVideoViewModel>();
        FilteredVideos = new ObservableCollection<SelectableVideoViewModel>();
        AvailableQualities = new ObservableCollection<StreamQualityOption>();
        DownloadQueue = new ObservableCollection<DownloadTaskViewModel>();
        AppSettings = SettingsManager.LoadSettings();
        AppSettings.ThemeMode = "Light";
        Videos.CollectionChanged += OnVideosCollectionChanged;
        FilteredVideos.CollectionChanged += (_, _) =>
        {
            RaiseResultStateChanged();
            RaiseSelectionStateChanged();
        };
        DownloadQueue.CollectionChanged += OnDownloadQueueChanged;
        Dispatcher.UIThread.Post(() => ApplyTheme(AppSettings.ThemeMode), Avalonia.Threading.DispatcherPriority.Loaded);
        
        _ = InitializeAppAsync();
    }

    private async Task InitializeAppAsync()
    {
        try
        {
            if (!FfmpegBootstrapper.TryConfigureExisting(out _))
            {
                IsInitializing = true;
                InitializationMessage = "Preparing required media components. Please wait...";
                await FfmpegBootstrapper.EnsureAvailableAsync(message =>
                    Dispatcher.UIThread.Post(() => InitializationMessage = message));
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

        RaiseSelectionStateChanged();
    }

    [ObservableProperty]
    private bool _isAllSelected;

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var video in FilteredVideos)
        {
            video.IsSelected = value;
        }

        RaiseSelectionStateChanged();
    }

    public ObservableCollection<SelectableVideoViewModel> Videos { get; }
    public ObservableCollection<SelectableVideoViewModel> FilteredVideos { get; }
    
    public ObservableCollection<StreamQualityOption> AvailableQualities { get; }
    
    public ObservableCollection<DownloadTaskViewModel> DownloadQueue { get; }

    public ObservableCollection<string> GlobalQualities { get; } = new ObservableCollection<string>
    {
        "Highest Quality MP3",
        "Highest Video (Native)",
        "1080p Video (Requires FFmpeg)",
        "720p Video (Requires FFmpeg)"
    };

    public ObservableCollection<string> ThemeModes { get; } = new ObservableCollection<string> { "Light" };

    [ObservableProperty]
    private string _selectedSection = "Downloader";

    public bool IsDownloaderSectionSelected => SelectedSection == "Downloader";
    public bool IsQueueSectionSelected => SelectedSection == "Queue";
    public bool IsSettingsSectionSelected => SelectedSection == "Settings";

    public int VideoCount => Videos.Count;
    public int FilteredVideoCount => FilteredVideos.Count;
    public bool HasResults => Videos.Count > 0;
    public bool HasFilteredVideos => FilteredVideos.Count > 0;
    public int SelectedDownloadCount => Videos.Count(v => v.IsSelected);
    public bool HasSelectedDownloads => SelectedDownloadCount > 0;
    public bool HasDownloaderActivity => HasSearched || HasResults;
    public bool ShowNoResultsState => HasSearched && !HasResults && !IsLoading;
    public bool IsVideoEmpty => HasSearched && HasResults && !IsVideoLoaded && !IsLoading;

    public int QueueItemCount => DownloadQueue.Count;
    public int ActiveDownloadCount => DownloadQueue.Count(q => !q.IsCompleted && !q.HasError);
    public int CompletedDownloadCount => DownloadQueue.Count(q => q.IsCompleted);
    public int FailedDownloadCount => DownloadQueue.Count(q => q.HasError);
    public bool HasQueueItems => DownloadQueue.Count > 0;
    public bool HasActiveDownloads => ActiveDownloadCount > 0;
    public bool HasFinishedDownloads => CompletedDownloadCount > 0 || FailedDownloadCount > 0;
    public bool HasFailedDownloads => FailedDownloadCount > 0;
    public string QueueBadgeText => ActiveDownloadCount > 0 ? ActiveDownloadCount.ToString() : DownloadQueue.Count.ToString();
    public string QueueSummary => DownloadQueue.Count == 0
        ? "No downloads queued"
        : $"{ActiveDownloadCount} active, {CompletedDownloadCount} completed, {FailedDownloadCount} failed";

    public string ThemeToggleLabel => "Light";

    [ObservableProperty]
    private string _selectedGlobalQuality = "Highest Quality MP3";

    public string SelectedGlobalQualitySummary => SelectedGlobalQuality switch
    {
        "Highest Quality MP3" => "Applies to every selected item. Converts the best audio to a 320 kbps MP3.",
        "Highest Video (Native)" => "Applies to every selected item. Saves the best native video stream.",
        "1080p Video (Requires FFmpeg)" => "Applies to every selected item. Uses 1080p when available.",
        "720p Video (Requires FFmpeg)" => "Applies to every selected item. Uses 720p when available.",
        _ => "Applies this choice to every selected item."
    };

    public string SelectedGlobalOutputType => IsAudioOnlyPreference(SelectedGlobalQuality)
        ? "MP3 audio"
        : "MP4 video";

    [ObservableProperty]
    private string _queueTabHeader = "Queue";

    private void UpdateQueueHeader()
    {
        var activeCount = DownloadQueue.Count(q => !q.IsCompleted && !q.HasError);
        QueueTabHeader = activeCount > 0 ? $"Queue ({activeCount})" : "Queue";
        RaiseQueueStateChanged();
    }

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsDownloaderSectionSelected));
        OnPropertyChanged(nameof(IsQueueSectionSelected));
        OnPropertyChanged(nameof(IsSettingsSectionSelected));
    }

    partial void OnSelectedGlobalQualityChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedGlobalQualitySummary));
        OnPropertyChanged(nameof(SelectedGlobalOutputType));
        QualitiesPlaceholder = SelectedGlobalOutputType;
    }

    [RelayCommand]
    private void SelectSection(string section)
    {
        SelectedSection = section;
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
    private bool _hasSearched = false;

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

    private void RaiseResultStateChanged()
    {
        OnPropertyChanged(nameof(VideoCount));
        OnPropertyChanged(nameof(FilteredVideoCount));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasFilteredVideos));
        OnPropertyChanged(nameof(HasDownloaderActivity));
        OnPropertyChanged(nameof(ShowNoResultsState));
        OnPropertyChanged(nameof(IsVideoEmpty));
    }

    private void RaiseSelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectedDownloadCount));
        OnPropertyChanged(nameof(HasSelectedDownloads));
    }

    private void OnVideosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (SelectableVideoViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnSelectableVideoPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (SelectableVideoViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnSelectableVideoPropertyChanged;
            }
        }

        RaiseResultStateChanged();
        RaiseSelectionStateChanged();
    }

    private void OnSelectableVideoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableVideoViewModel.IsSelected))
        {
            RaiseSelectionStateChanged();
        }
    }

    private void OnDownloadQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DownloadTaskViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnDownloadItemPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (DownloadTaskViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnDownloadItemPropertyChanged;
            }
        }

        UpdateQueueHeader();
    }

    private void OnDownloadItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTaskViewModel.IsCompleted)
            or nameof(DownloadTaskViewModel.HasError)
            or nameof(DownloadTaskViewModel.Progress)
            or nameof(DownloadTaskViewModel.Status))
        {
            RaiseQueueStateChanged();
        }
    }

    private void RaiseQueueStateChanged()
    {
        OnPropertyChanged(nameof(QueueItemCount));
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(CompletedDownloadCount));
        OnPropertyChanged(nameof(FailedDownloadCount));
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(HasActiveDownloads));
        OnPropertyChanged(nameof(HasFinishedDownloads));
        OnPropertyChanged(nameof(HasFailedDownloads));
        OnPropertyChanged(nameof(QueueBadgeText));
        OnPropertyChanged(nameof(QueueSummary));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoResultsState));
        OnPropertyChanged(nameof(IsVideoEmpty));
    }

    partial void OnIsVideoLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVideoEmpty));
    }

    partial void OnHasSearchedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasDownloaderActivity));
        OnPropertyChanged(nameof(ShowNoResultsState));
        OnPropertyChanged(nameof(IsVideoEmpty));
    }

    [RelayCommand]
    private void SelectVisibleVideos()
    {
        foreach (var video in FilteredVideos)
        {
            video.IsSelected = true;
        }

        IsAllSelected = FilteredVideos.Count > 0;
        RaiseSelectionStateChanged();
    }

    [RelayCommand]
    private void ClearVisibleSelection()
    {
        foreach (var video in FilteredVideos)
        {
            video.IsSelected = false;
        }

        IsAllSelected = false;
        RaiseSelectionStateChanged();
    }

    [RelayCommand]
    private void RetryFailedDownloads()
    {
        var failedItems = DownloadQueue
            .Where(q => q.HasError && !string.IsNullOrWhiteSpace(q.VideoUrl))
            .ToList();

        if (failedItems.Count == 0)
        {
            StatusMessage = "No failed downloads to retry.";
            return;
        }

        foreach (var item in failedItems)
        {
            item.ResetForRetry();
            _ = ProcessDownloadAsync(item, item.VideoUrl, item.QualityLabel);
        }

        StatusMessage = failedItems.Count == 1
            ? "Retrying 1 failed download."
            : $"Retrying {failedItems.Count} failed downloads.";
        UpdateQueueHeader();
    }

    [RelayCommand]
    private void ClearFinishedDownloads()
    {
        var finishedItems = DownloadQueue
            .Where(q => q.IsCompleted || q.HasError)
            .ToList();

        foreach (var item in finishedItems)
        {
            DownloadQueue.Remove(item);
        }

        if (finishedItems.Count > 0)
        {
            StatusMessage = finishedItems.Count == 1
                ? "Cleared 1 finished queue item."
                : $"Cleared {finishedItems.Count} finished queue items.";
        }

        UpdateQueueHeader();
    }

    [RelayCommand]
    private void ResetDownloaderState()
    {
        if (IsLoading) return;

        SearchQuery = string.Empty;
        Videos.Clear();
        FilteredVideos.Clear();
        PlaylistSearchQuery = string.Empty;
        IsAllSelected = false;
        SelectedVideo = null;
        ClearSelectedVideoDetails();
        AvailableQualities.Clear();
        IsPlaylistLoaded = false;
        IsLoadingQualities = false;
        HasSearched = false;
        StatusMessage = "Ready";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        AppSettings.ThemeMode = "Light";
        SettingsManager.SaveSettings(AppSettings);
        ApplyTheme("Light");
        StatusMessage = "Settings Saved successfully!";
        OnPropertyChanged(nameof(ThemeToggleLabel));
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        AppSettings = CloneSettings(AppSettings, themeMode: "Light");
        SettingsManager.SaveSettings(AppSettings);
        ApplyTheme("Light");
        StatusMessage = "Light appearance enabled.";
    }

    private static AppSettings CloneSettings(
        AppSettings source,
        string? downloadPath = null,
        int? concurrentDownloads = null,
        string? themeMode = null,
        bool? autoTagAudio = null,
        bool? downloadSubtitles = null,
        bool? embedSubtitles = null,
        string? subtitleLanguage = null)
    {
        return new AppSettings
        {
            DownloadPath = downloadPath ?? source.DownloadPath,
            ConcurrentDownloads = concurrentDownloads ?? source.ConcurrentDownloads,
            ThemeMode = themeMode ?? source.ThemeMode,
            AutoTagAudio = autoTagAudio ?? source.AutoTagAudio,
            DownloadSubtitles = downloadSubtitles ?? source.DownloadSubtitles,
            EmbedSubtitles = embedSubtitles ?? source.EmbedSubtitles,
            SubtitleLanguage = subtitleLanguage ?? source.SubtitleLanguage
        };
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
                AppSettings = CloneSettings(AppSettings, downloadPath: path);
                StatusMessage = "Download path updated.";
            }
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        HasSearched = true;
        IsLoading = true;
        SelectedVideo = null;
        IsVideoLoaded = false;
        IsPlaylistLoaded = false;
        Videos.Clear();
        FilteredVideos.Clear();
        PlaylistSearchQuery = string.Empty;
        AvailableQualities.Clear();
        ClearSelectedVideoDetails();
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

            if (!Videos.Any())
            {
                StatusMessage = "No results found.";
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

    partial void OnSelectedVideoChanged(SelectableVideoViewModel? value)
    {
        if (value == null)
        {
            ClearSelectedVideoDetails();
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
        IsLoadingQualities = false;
        QualitiesPlaceholder = SelectedGlobalOutputType;
        StatusMessage = "Choose one download option. It applies to every selected item.";
    }

    private void ClearSelectedVideoDetails()
    {
        VideoTitle = string.Empty;
        VideoAuthor = string.Empty;
        VideoDuration = string.Empty;
        ThumbnailUrl = string.Empty;
        SelectedQuality = null;
        IsVideoLoaded = false;
        IsLoadingQualities = false;
        QualitiesPlaceholder = SelectedGlobalOutputType;
    }

    private SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(3, 3);
    private const int MaxDownloadAttempts = 2;

    partial void OnAppSettingsChanged(AppSettings value)
    {
        var concurrentDownloads = Math.Max(1, value.ConcurrentDownloads);
        _downloadSemaphore = new SemaphoreSlim(concurrentDownloads, concurrentDownloads);
        OnPropertyChanged(nameof(ThemeToggleLabel));
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
            EnqueueDownload(vid.Video, SelectedGlobalQuality);
        }
    }

    private void EnqueueDownload(IVideo video, string qualityPreference)
    {
        var safeTitle = string.Join("_", video.Title.Split(System.IO.Path.GetInvalidFileNameChars()));
        var isAudioOnly = IsAudioOnlyPreference(qualityPreference);
        var ext = isAudioOnly ? ".mp3" : ".mp4";
        var filePath = System.IO.Path.Combine(AppSettings.DownloadPath, $"{safeTitle}{ext}");

        var thumbnailUrl = video.Thumbnails.Count > 0
            ? video.Thumbnails.OrderByDescending(t => t.Resolution.Area).First().Url
            : string.Empty;
        var taskVm = new DownloadTaskViewModel(video.Title, null, filePath, thumbnailUrl, qualityPreference, video.Url);
        DownloadQueue.Add(taskVm);
        UpdateQueueHeader();

        _ = ProcessDownloadAsync(taskVm, video.Url, qualityPreference);
    }

    private async Task ProcessDownloadAsync(DownloadTaskViewModel taskVm, string videoUrl, string qualityPreference)
    {
        await _downloadSemaphore.WaitAsync();
        try
        {
            for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
            {
                try
                {
                    await RunDownloadAttemptAsync(taskVm, videoUrl, qualityPreference, attempt);
                    return;
                }
                catch (Exception ex) when (attempt < MaxDownloadAttempts && IsTransientDownloadException(ex))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        taskVm.Progress = 0;
                        taskVm.Status = "Network slowed down. Retrying...";
                    });

                    await Task.Delay(TimeSpan.FromSeconds(2), taskVm.CancellationTokenSource.Token);
                }
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                taskVm.HasError = true;
                taskVm.Status = GetFriendlyDownloadError(ex);
                UpdateQueueHeader();
            });
        }
        finally
        {
            _downloadSemaphore.Release();
            Dispatcher.UIThread.Post(() => UpdateQueueHeader());
        }
    }

    private async Task RunDownloadAttemptAsync(
        DownloadTaskViewModel taskVm,
        string videoUrl,
        string qualityPreference,
        int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            taskVm.HasError = false;
            taskVm.IsCompleted = false;
            if (attempt > 1)
            {
                taskVm.Status = $"Retrying download ({attempt}/{MaxDownloadAttempts})...";
            }
        });

        var qualities = await _videoDownloader.GetAvailableQualitiesAsync(videoUrl);
        StreamQualityOption? targetQuality = qualities.FirstOrDefault(q => q.DisplayName == qualityPreference);
        if (targetQuality == null)
        {
            if (IsHighestAudioPreference(qualityPreference)) targetQuality = qualities.FirstOrDefault(q => q.DisplayName.Contains("Audio Only"));
            else if (qualityPreference == "Lowest Audio Only") targetQuality = qualities.LastOrDefault(q => q.DisplayName.Contains("Audio Only"));
            else if (qualityPreference == "1080p Video (Requires FFmpeg)") targetQuality = qualities.FirstOrDefault(q => q.DisplayName.Contains("1080p")) ?? qualities.FirstOrDefault(q => !q.DisplayName.Contains("Audio"));
            else if (qualityPreference == "720p Video (Requires FFmpeg)") targetQuality = qualities.FirstOrDefault(q => q.DisplayName.Contains("720p")) ?? qualities.FirstOrDefault(q => !q.DisplayName.Contains("Audio"));
            else targetQuality = qualities.FirstOrDefault(q => !q.RequiresFfmpeg && !q.DisplayName.Contains("Audio Only"));
        }
        if (targetQuality == null) targetQuality = qualities.FirstOrDefault();
        
        if (targetQuality == null)
        {
            throw new InvalidOperationException("Could not find a matching stream quality.");
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

        var isAudioOnly = targetQuality.DisplayName.Contains("Audio Only");
        var needsFfmpeg = targetQuality.RequiresFfmpeg || (isAudioOnly && taskVm.FilePath.EndsWith(".mp3")) || AppSettings.DownloadSubtitles;

        if (needsFfmpeg)
        {
            Dispatcher.UIThread.Post(() => taskVm.Status = "Preparing FFmpeg...");
            await FfmpegBootstrapper.EnsureAvailableAsync(
                message => Dispatcher.UIThread.Post(() => taskVm.Status = message),
                taskVm.CancellationTokenSource.Token);
        }

        await _videoDownloader.DownloadAsync(targetQuality, taskVm.FilePath, progress, taskVm.CancellationTokenSource.Token);
        
        if (AppSettings.DownloadSubtitles && !isAudioOnly)
        {
            Dispatcher.UIThread.Post(() => taskVm.Status = "Downloading Subtitles...");
            var vttPath = System.IO.Path.ChangeExtension(taskVm.FilePath, ".vtt");
            var downloadedSub = await _videoDownloader.DownloadSubtitlesAsync(videoUrl, vttPath, AppSettings.SubtitleLanguage);

            if (downloadedSub == null)
            {
                Dispatcher.UIThread.Post(() => taskVm.Status = "Warning: No subtitles found for this video on YouTube.");
                await Task.Delay(2500);
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
            Dispatcher.UIThread.Post(() => taskVm.Status = "Writing Apple Music-ready metadata...");
            try
            {
                var videoInfo = await _videoDownloader.GetVideoDetailsAsync(videoUrl);
                var thumbUrl = videoInfo.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url;
                await _audioMetadataService.ApplyYoutubeAudioMetadataAsync(
                    taskVm.FilePath,
                    taskVm.Title,
                    videoInfo.Author.ChannelTitle,
                    thumbUrl,
                    videoUrl,
                    taskVm.CancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => taskVm.Status = "Metadata warning: " + ex.Message);
                await Task.Delay(2000);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            taskVm.Status = "Completed";
            taskVm.IsCompleted = true;
            taskVm.Progress = 100;
            UpdateQueueHeader();
        });
    }

    private static bool IsTransientDownloadException(Exception ex)
    {
        return ex is HttpRequestException
               || ex is TimeoutException
               || ex is TaskCanceledException
               || (ex.InnerException != null && IsTransientDownloadException(ex.InnerException));
    }

    private static string GetFriendlyDownloadError(Exception ex)
    {
        if (IsTransientDownloadException(ex)
            || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Network timeout while downloading. Use Retry Failed to try again.";
        }

        return "Error: " + ex.Message;
    }

    private static bool IsAudioOnlyPreference(string qualityPreference)
    {
        return qualityPreference.Contains("Audio", StringComparison.OrdinalIgnoreCase)
               || qualityPreference.Contains("MP3", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHighestAudioPreference(string qualityPreference)
    {
        return qualityPreference.Equals("Highest Quality MP3", StringComparison.OrdinalIgnoreCase)
               || qualityPreference.Equals("Highest Audio Only", StringComparison.OrdinalIgnoreCase);
    }
}
