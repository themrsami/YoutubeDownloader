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
using System.Text.Json;
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
        LoadPersistedQueue();
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
    public int ActiveDownloadCount => DownloadQueue.Count(q => q.State is DownloadTaskState.Pending or DownloadTaskState.Running);
    public int RunningDownloadCount => DownloadQueue.Count(q => q.State == DownloadTaskState.Running);
    public int PausedDownloadCount => DownloadQueue.Count(q => q.State == DownloadTaskState.Paused);
    public int CompletedDownloadCount => DownloadQueue.Count(q => q.IsCompleted);
    public int FailedDownloadCount => DownloadQueue.Count(q => q.HasError);
    public int CanceledDownloadCount => DownloadQueue.Count(q => q.State == DownloadTaskState.Canceled);
    public bool HasQueueItems => DownloadQueue.Count > 0;
    public bool HasActiveDownloads => ActiveDownloadCount > 0;
    public bool HasPausedDownloads => PausedDownloadCount > 0;
    public bool HasPausableDownloads => DownloadQueue.Any(q => q.CanPause);
    public bool HasResumableDownloads => DownloadQueue.Any(q => q.CanResume);
    public bool HasCancellableDownloads => DownloadQueue.Any(q => q.CanCancel);
    public bool HasFinishedDownloads => CompletedDownloadCount > 0 || FailedDownloadCount > 0 || CanceledDownloadCount > 0;
    public bool HasFailedDownloads => FailedDownloadCount > 0;
    public string QueueBadgeText => ActiveDownloadCount > 0 ? ActiveDownloadCount.ToString() : DownloadQueue.Count.ToString();
    public string QueueSummary => DownloadQueue.Count == 0
        ? "No downloads queued"
        : $"{RunningDownloadCount} running, {ActiveDownloadCount} queued/active, {PausedDownloadCount} paused, {CompletedDownloadCount} done, {FailedDownloadCount} failed";

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
        var activeCount = DownloadQueue.Count(q => q.State is DownloadTaskState.Pending or DownloadTaskState.Running);
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
        OnPropertyChanged(nameof(IsAudioMetadataEditorVisible));
        QualitiesPlaceholder = SelectedGlobalOutputType;
    }

    partial void OnCurrentResultKindChanged(QueryResultKind value)
    {
        OnPropertyChanged(nameof(CurrentResultKindLabel));
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
    private QueryResultKind _currentResultKind = QueryResultKind.Video;

    [ObservableProperty]
    private string _currentCollectionTitle = string.Empty;

    [ObservableProperty]
    private bool _isLoadingQualities = false;

    [ObservableProperty]
    private string _qualitiesPlaceholder = "Select Quality...";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public string CurrentResultKindLabel => CurrentResultKind switch
    {
        QueryResultKind.Video => "Video",
        QueryResultKind.Playlist => "Playlist",
        QueryResultKind.Channel => "Channel",
        QueryResultKind.Search => "Search",
        _ => "Results"
    };

    public bool IsAudioMetadataEditorVisible => IsVideoLoaded && IsAudioOnlyPreference(SelectedGlobalQuality);

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
                AttachQueueItem(item);
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
        ScheduleSaveQueueState();
    }

    private void AttachQueueItem(DownloadTaskViewModel item)
    {
        item.PropertyChanged += OnDownloadItemPropertyChanged;
        item.PauseRequestedAsync = PauseQueueItemAsync;
        item.ResumeRequestedAsync = ResumeQueueItemAsync;
        item.CancelRequestedAsync = CancelQueueItemAsync;
        item.RetryRequestedAsync = RetryQueueItemAsync;
        item.RemoveRequestedAsync = RemoveQueueItemAsync;
        item.ChooseArtworkRequestedAsync = ChooseQueueItemArtworkAsync;
    }

    private void OnDownloadItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTaskViewModel.State)
            or nameof(DownloadTaskViewModel.IsCompleted)
            or nameof(DownloadTaskViewModel.HasError)
            or nameof(DownloadTaskViewModel.Progress)
            or nameof(DownloadTaskViewModel.Status))
        {
            RaiseQueueStateChanged();
        }

        if (e.PropertyName is nameof(DownloadTaskViewModel.State)
            or nameof(DownloadTaskViewModel.Status)
            or nameof(DownloadTaskViewModel.Progress)
            or nameof(DownloadTaskViewModel.FilePath)
            or nameof(DownloadTaskViewModel.Metadata))
        {
            ScheduleSaveQueueState();
        }
    }

    private void RaiseQueueStateChanged()
    {
        OnPropertyChanged(nameof(QueueItemCount));
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(RunningDownloadCount));
        OnPropertyChanged(nameof(PausedDownloadCount));
        OnPropertyChanged(nameof(CompletedDownloadCount));
        OnPropertyChanged(nameof(FailedDownloadCount));
        OnPropertyChanged(nameof(CanceledDownloadCount));
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(HasActiveDownloads));
        OnPropertyChanged(nameof(HasPausedDownloads));
        OnPropertyChanged(nameof(HasPausableDownloads));
        OnPropertyChanged(nameof(HasResumableDownloads));
        OnPropertyChanged(nameof(HasCancellableDownloads));
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
        OnPropertyChanged(nameof(IsAudioMetadataEditorVisible));
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
    private async Task RetryFailedDownloads()
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
            await RetryQueueItemAsync(item);
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
            .Where(q => q.IsCompleted || q.HasError || q.State == DownloadTaskState.Canceled)
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
    private async Task PauseAllDownloads()
    {
        var items = DownloadQueue.Where(q => q.CanPause).ToList();
        foreach (var item in items)
        {
            await PauseQueueItemAsync(item);
        }

        StatusMessage = items.Count == 0
            ? "No active downloads to pause."
            : $"Paused {items.Count} queue item(s). Active items will restart from the beginning when resumed.";
    }

    [RelayCommand]
    private async Task ResumeAllDownloads()
    {
        var items = DownloadQueue.Where(q => q.CanResume).ToList();
        foreach (var item in items)
        {
            await ResumeQueueItemAsync(item);
        }

        StatusMessage = items.Count == 0
            ? "No paused downloads to resume."
            : $"Resumed {items.Count} queue item(s).";
    }

    [RelayCommand]
    private async Task CancelAllDownloads()
    {
        var items = DownloadQueue.Where(q => q.CanCancel).ToList();
        foreach (var item in items)
        {
            await CancelQueueItemAsync(item);
        }

        StatusMessage = items.Count == 0
            ? "No downloads to cancel."
            : $"Canceled {items.Count} queue item(s).";
    }

    private Task PauseQueueItemAsync(DownloadTaskViewModel item)
    {
        if (!DownloadQueue.Contains(item) || !item.CanPause)
        {
            return Task.CompletedTask;
        }

        item.RequestPause();
        if (item.State == DownloadTaskState.Pending)
        {
            item.MarkPaused("Paused before starting.");
        }
        else
        {
            item.MarkPaused("Pausing active download. Resume restarts from the beginning.");
        }

        StatusMessage = "Paused queue item. Active stream downloads restart from the beginning when resumed.";
        UpdateQueueHeader();
        ScheduleSaveQueueState();
        return Task.CompletedTask;
    }

    private Task ResumeQueueItemAsync(DownloadTaskViewModel item)
    {
        if (!DownloadQueue.Contains(item) || !item.CanResume)
        {
            return Task.CompletedTask;
        }

        if (item.IsProcessing)
        {
            StatusMessage = "That item is still pausing. Resume it again in a moment.";
            return Task.CompletedTask;
        }

        item.ResetForResume();
        StatusMessage = "Resumed queue item. It will restart from the beginning.";
        UpdateQueueHeader();
        ScheduleSaveQueueState();
        return StartQueueItemAsync(item);
    }

    private Task CancelQueueItemAsync(DownloadTaskViewModel item)
    {
        if (!DownloadQueue.Contains(item) || !item.CanCancel)
        {
            return Task.CompletedTask;
        }

        item.RequestCancel();
        item.MarkCanceled();
        StatusMessage = "Canceled queue item.";
        UpdateQueueHeader();
        ScheduleSaveQueueState();
        return Task.CompletedTask;
    }

    private Task RetryQueueItemAsync(DownloadTaskViewModel item)
    {
        if (!DownloadQueue.Contains(item) || !item.CanRetry)
        {
            return Task.CompletedTask;
        }

        if (item.IsProcessing)
        {
            StatusMessage = "That item is still finishing. Retry it again in a moment.";
            return Task.CompletedTask;
        }

        item.ResetForRetry();
        StatusMessage = "Retrying queue item.";
        UpdateQueueHeader();
        ScheduleSaveQueueState();
        return StartQueueItemAsync(item);
    }

    private Task RemoveQueueItemAsync(DownloadTaskViewModel item)
    {
        if (!DownloadQueue.Contains(item) || !item.CanRemove)
        {
            return Task.CompletedTask;
        }

        if (item.State is DownloadTaskState.Pending or DownloadTaskState.Paused)
        {
            item.RequestCancel();
        }

        DownloadQueue.Remove(item);
        StatusMessage = "Removed queue item.";
        UpdateQueueHeader();
        ScheduleSaveQueueState();
        return Task.CompletedTask;
    }

    private async Task ChooseQueueItemArtworkAsync(DownloadTaskViewModel item)
    {
        if (!DownloadQueue.Contains(item) || !item.IsAudio || SelectArtworkFileAction == null)
        {
            return;
        }

        var path = await SelectArtworkFileAction();
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.Metadata.ArtworkFilePath = path;
            StatusMessage = "Artwork override selected for queued item.";
            ScheduleSaveQueueState();
        }
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
        CurrentResultKind = QueryResultKind.Video;
        CurrentCollectionTitle = string.Empty;
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
    public Func<Task<string?>>? SelectArtworkFileAction { get; set; }

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
    private async Task ChooseSelectedArtwork()
    {
        if (SelectedVideo == null || SelectArtworkFileAction == null)
        {
            return;
        }

        var path = await SelectArtworkFileAction();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SelectedVideo.Metadata.ArtworkFilePath = path;
            StatusMessage = "Artwork override selected for this item.";
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
        CurrentResultKind = QueryResultKind.Video;
        CurrentCollectionTitle = string.Empty;
        StatusMessage = "Searching YouTube...";

        try
        {
            var result = await _queryResolver.ResolveAsync(
                SearchQuery,
                async item =>
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CurrentResultKind = item.Kind;
                        CurrentCollectionTitle = item.CollectionTitle;
                        IsPlaylistLoaded = item.Kind is QueryResultKind.Playlist or QueryResultKind.Channel or QueryResultKind.Search;
                        AddResolvedVideo(item.Video, item.Kind, item.CollectionTitle, item.Index);
                        if (SelectedVideo == null && FilteredVideos.Any())
                        {
                            SelectedVideo = FilteredVideos.First();
                        }

                        if (Videos.Count == 1)
                        {
                            IsLoading = false;
                        }
                    });
                },
                async message =>
                {
                    await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = message);
                });

            CurrentResultKind = result.Kind;
            CurrentCollectionTitle = result.Title;
            
            if (Videos.Count == 0)
            {
                var index = 0;
                foreach (var video in result.Videos)
                {
                    index++;
                    AddResolvedVideo(video, result.Kind, result.Title, index);
                }
            }

            if (result.IsCollection)
            {
                IsPlaylistLoaded = true;
                StatusMessage = string.IsNullOrWhiteSpace(result.WarningMessage)
                    ? $"{CurrentResultKindLabel} loaded: {result.Title} ({Videos.Count} videos)"
                    : result.WarningMessage;
                if (FilteredVideos.Any()) SelectedVideo = FilteredVideos.First();
            }
            else
            {
                var video = result.Videos.FirstOrDefault();
                if (video != null)
                {
                    if (Videos.Count == 0)
                    {
                        AddResolvedVideo(video, QueryResultKind.Video, string.Empty, 1);
                    }

                    SelectedVideo = FilteredVideos.FirstOrDefault();
                    StatusMessage = string.IsNullOrWhiteSpace(result.WarningMessage)
                        ? "Video loaded successfully."
                        : result.WarningMessage;
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

    private void AddResolvedVideo(IVideo video, QueryResultKind kind, string collectionTitle, int index)
    {
        if (Videos.Any(v => v.Video.Id == video.Id))
        {
            return;
        }

        var vm = new SelectableVideoViewModel(video, kind, collectionTitle, index);
        Videos.Add(vm);
        FilteredVideos.Add(vm);
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
        OnPropertyChanged(nameof(IsAudioMetadataEditorVisible));
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
    private CancellationTokenSource? _queueSaveDebounce;
    private bool _isLoadingPersistedQueue;
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
            EnqueueDownload(vid, SelectedGlobalQuality);
        }
    }

    private void EnqueueDownload(SelectableVideoViewModel item, string qualityPreference)
    {
        var video = item.Video;
        var safeTitle = SanitizeFileName(video.Title);
        var isAudioOnly = IsAudioOnlyPreference(qualityPreference);
        var ext = isAudioOnly ? ".mp3" : ".mp4";
        var outputDirectory = AppSettings.DownloadPath;

        if (item.SourceKind is QueryResultKind.Playlist or QueryResultKind.Channel)
        {
            var folderName = SanitizeFileName(string.IsNullOrWhiteSpace(item.CollectionTitle)
                ? item.SourceKind.ToString()
                : item.CollectionTitle);
            outputDirectory = System.IO.Path.Combine(outputDirectory, folderName);
        }

        var orderedTitle = item.CollectionIndex > 0
                           && item.SourceKind is (QueryResultKind.Playlist or QueryResultKind.Channel)
            ? $"{item.CollectionIndex:000} - {safeTitle}"
            : safeTitle;

        var filePath = GetUniqueOutputPath(outputDirectory, $"{orderedTitle}{ext}");

        var thumbnailUrl = video.Thumbnails.Count > 0
            ? video.Thumbnails.OrderByDescending(t => t.Resolution.Area).First().Url
            : string.Empty;

        var metadata = item.Metadata.Clone();
        if (string.IsNullOrWhiteSpace(metadata.SourceUrl))
        {
            metadata.SourceUrl = video.Url;
        }

        if (string.IsNullOrWhiteSpace(metadata.ArtworkUrl))
        {
            metadata.ArtworkUrl = thumbnailUrl;
        }

        var taskVm = new DownloadTaskViewModel(
            video.Title,
            null,
            filePath,
            thumbnailUrl,
            qualityPreference,
            video.Url,
            metadata,
            isAudioOnly,
            item.SourceKind,
            item.CollectionTitle,
            item.CollectionIndex);
        DownloadQueue.Add(taskVm);
        UpdateQueueHeader();

        _ = StartQueueItemAsync(taskVm);
    }

    private Task StartQueueItemAsync(DownloadTaskViewModel taskVm)
    {
        if (taskVm.State is DownloadTaskState.Completed or DownloadTaskState.Running)
        {
            return Task.CompletedTask;
        }

        if (!taskVm.TryBeginProcessing())
        {
            StatusMessage = "That queue item is already scheduled.";
            return Task.CompletedTask;
        }

        _ = ProcessDownloadAsync(taskVm);
        return Task.CompletedTask;
    }

    private async Task ProcessDownloadAsync(DownloadTaskViewModel taskVm)
    {
        var semaphore = _downloadSemaphore;
        var acquiredSemaphore = false;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (taskVm.State == DownloadTaskState.Pending)
                {
                    taskVm.Status = "Queued";
                }
            });

            await semaphore.WaitAsync(taskVm.CancellationTokenSource.Token);
            acquiredSemaphore = true;

            if (taskVm.State is DownloadTaskState.Paused or DownloadTaskState.Canceled
                || taskVm.CancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => taskVm.MarkRunning());

            for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
            {
                try
                {
                    await RunDownloadAttemptAsync(taskVm, taskVm.VideoUrl, taskVm.QualityLabel, attempt);
                    return;
                }
                catch (OperationCanceledException) when (taskVm.IsPauseRequested || taskVm.IsCancelRequested)
                {
                    throw;
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
        catch (OperationCanceledException) when (taskVm.IsPauseRequested)
        {
            Dispatcher.UIThread.Post(() =>
            {
                taskVm.MarkPaused();
                UpdateQueueHeader();
            });
        }
        catch (OperationCanceledException) when (taskVm.IsCancelRequested)
        {
            Dispatcher.UIThread.Post(() =>
            {
                taskVm.MarkCanceled();
                UpdateQueueHeader();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (taskVm.IsPauseRequested)
                {
                    taskVm.MarkPaused();
                }
                else if (taskVm.IsCancelRequested)
                {
                    taskVm.MarkCanceled();
                }
                else
                {
                    taskVm.MarkFailed(GetFriendlyDownloadError(ex));
                }

                UpdateQueueHeader();
            });
        }
        finally
        {
            if (acquiredSemaphore)
            {
                semaphore.Release();
            }

            taskVm.EndProcessing();
            Dispatcher.UIThread.Post(() =>
            {
                UpdateQueueHeader();
                ScheduleSaveQueueState();
            });
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
            if (attempt > 1)
            {
                taskVm.MarkRunning($"Retrying download ({attempt}/{MaxDownloadAttempts})...");
            }
            else
            {
                taskVm.MarkRunning();
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

        if (taskVm.IsAudio && AppSettings.AutoTagAudio)
        {
            Dispatcher.UIThread.Post(() => taskVm.Status = "Writing Apple Music-ready metadata...");
            try
            {
                if (string.IsNullOrWhiteSpace(taskVm.Metadata.Artist)
                    || string.IsNullOrWhiteSpace(taskVm.Metadata.ArtworkUrl))
                {
                    var videoInfo = await _videoDownloader.GetVideoDetailsAsync(videoUrl);
                    if (string.IsNullOrWhiteSpace(taskVm.Metadata.Artist))
                    {
                        taskVm.Metadata.Artist = videoInfo.Author.ChannelTitle;
                    }

                    if (string.IsNullOrWhiteSpace(taskVm.Metadata.ArtworkUrl))
                    {
                        taskVm.Metadata.ArtworkUrl = videoInfo.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty;
                    }
                }

                await _audioMetadataService.ApplyYoutubeAudioMetadataAsync(
                    taskVm.FilePath,
                    taskVm.Metadata.ToModel(),
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
            taskVm.MarkCompleted();
            UpdateQueueHeader();
        });
    }

    private void LoadPersistedQueue()
    {
        if (!File.Exists(AppPaths.QueueStateFile))
        {
            return;
        }

        try
        {
            _isLoadingPersistedQueue = true;
            var json = File.ReadAllText(AppPaths.QueueStateFile);
            var snapshot = JsonSerializer.Deserialize<DownloadQueueSnapshot>(json);
            if (snapshot?.Items == null)
            {
                return;
            }

            foreach (var item in snapshot.Items)
            {
                var restoredState = item.State is DownloadTaskState.Pending or DownloadTaskState.Running
                    ? DownloadTaskState.Paused
                    : item.State;

                var restoredStatus = item.State is DownloadTaskState.Pending or DownloadTaskState.Running
                    ? "Paused after app restart. Resume restarts from the beginning."
                    : item.Status;

                var taskVm = new DownloadTaskViewModel(
                    item.Title,
                    null,
                    item.FilePath,
                    item.ThumbnailUrl,
                    item.QualityLabel,
                    item.VideoUrl,
                    AudioMetadataViewModel.FromModel(item.Metadata ?? new AudioMetadata()),
                    item.IsAudio,
                    item.SourceKind,
                    item.CollectionTitle,
                    item.CollectionIndex,
                    item.Id,
                    restoredState,
                    restoredState == DownloadTaskState.Paused ? 0 : item.Progress,
                    restoredStatus);

                DownloadQueue.Add(taskVm);
            }

            if (DownloadQueue.Count > 0)
            {
                StatusMessage = $"Restored {DownloadQueue.Count} queue item(s). Paused items will not restart until resumed.";
            }
        }
        catch
        {
            StatusMessage = "Could not restore the saved download queue.";
        }
        finally
        {
            _isLoadingPersistedQueue = false;
            UpdateQueueHeader();
            ScheduleSaveQueueState();
        }
    }

    private void ScheduleSaveQueueState()
    {
        if (_isLoadingPersistedQueue)
        {
            return;
        }

        try
        {
            _queueSaveDebounce?.Cancel();
            var cts = new CancellationTokenSource();
            _queueSaveDebounce = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, cts.Token);
                    var snapshot = await Dispatcher.UIThread.InvokeAsync(CreateQueueSnapshot);
                    await SaveQueueSnapshotAsync(snapshot, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }

    private DownloadQueueSnapshot CreateQueueSnapshot()
    {
        return new DownloadQueueSnapshot
        {
            Items = DownloadQueue.Select(item => new DownloadQueueItemSnapshot
            {
                Id = item.Id,
                Title = item.Title,
                FilePath = item.FilePath,
                ThumbnailUrl = item.ThumbnailUrl,
                QualityLabel = item.QualityLabel,
                VideoUrl = item.VideoUrl,
                State = item.State,
                Progress = item.Progress,
                Status = item.Status,
                IsAudio = item.IsAudio,
                SourceKind = item.SourceKind,
                CollectionTitle = item.CollectionTitle,
                CollectionIndex = item.CollectionIndex,
                Metadata = item.Metadata.ToModel()
            }).ToList()
        };
    }

    private static async Task SaveQueueSnapshotAsync(DownloadQueueSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(AppPaths.QueueStateFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(AppPaths.QueueStateFile, json, cancellationToken);
    }

    private string GetUniqueOutputPath(string outputDirectory, string fileName)
    {
        Directory.CreateDirectory(outputDirectory);

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(outputDirectory, fileName);
        var index = 2;

        while (File.Exists(candidate) || DownloadQueue.Any(q => string.Equals(q.FilePath, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = Path.Combine(outputDirectory, $"{baseName} ({index}){extension}");
            index++;
        }

        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = string.Join("_", value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned.Trim();
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
