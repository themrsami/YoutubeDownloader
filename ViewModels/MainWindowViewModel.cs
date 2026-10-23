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
using System.Net.Sockets;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PremiumYoutubeDownloader.Models;
using PremiumYoutubeDownloader.Services;
using Avalonia.Threading;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.ClosedCaptions;
using Material.Styles.Themes;
using Material.Colors;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions QueueJsonOptions = new() { WriteIndented = true };

    private readonly QueryResolverService _queryResolver;
    private readonly VideoDownloaderService _videoDownloader;
    private readonly AudioMetadataService _audioMetadataService;
    private readonly DownloadSlotLimiter _downloadSlots;
    private readonly HashSet<string> _resolvedVideoIds = new(StringComparer.Ordinal);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _settingsSaveDebounce;
    private CancellationTokenSource? _queueSaveDebounce;
    private bool _isLoadingPersistedQueue;
    private bool _isQuitConfirmed;
    private int _selectedCount;
    private const int MaxDownloadAttempts = 2;

    [ObservableProperty]
    private bool _isInitializing;

    [ObservableProperty]
    private string _initializationMessage = "Initializing...";

    public MainWindowViewModel()
    {
        _queryResolver = new QueryResolverService();
        _videoDownloader = new VideoDownloaderService();
        _audioMetadataService = new AudioMetadataService();
        Videos = new ObservableCollection<SelectableVideoViewModel>();
        FilteredVideos = new ObservableCollection<SelectableVideoViewModel>();
        DownloadQueue = new ObservableCollection<DownloadTaskViewModel>();
        _appSettings = SettingsManager.LoadSettings();
        _appSettings.PropertyChanged += OnAppSettingsPropertyChanged;
        _downloadSlots = new DownloadSlotLimiter(_appSettings.ConcurrentDownloads);
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
            AppLog.Error("FFmpeg bootstrap", ex);
            InitializationMessage = "Could not download FFmpeg: " + ex.Message
                                    + " MP3 and HD downloads will try again when you start one.";
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
            if (MatchesFilter(v, value))
            {
                FilteredVideos.Add(v);
            }
        }

        RaiseSelectionStateChanged();
    }

    private static bool MatchesFilter(SelectableVideoViewModel video, string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
               || video.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || video.Author.Contains(filter, StringComparison.OrdinalIgnoreCase);
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

    public ObservableCollection<DownloadTaskViewModel> DownloadQueue { get; }

    public ObservableCollection<string> GlobalQualities { get; } = new ObservableCollection<string>
    {
        VideoDownloaderService.HighestMp3Preference,
        VideoDownloaderService.BestVideoPreference,
        VideoDownloaderService.Video1080pPreference,
        VideoDownloaderService.Video720pPreference,
        VideoDownloaderService.NativeVideoPreference
    };

    public IReadOnlyList<int> ConcurrentDownloadOptions { get; } =
        Enumerable.Range(SettingsManager.MinConcurrentDownloads, SettingsManager.MaxConcurrentDownloads).ToList();

    [ObservableProperty]
    private string _selectedSection = "Downloader";

    public bool IsDownloaderSectionSelected => SelectedSection == "Downloader";
    public bool IsQueueSectionSelected => SelectedSection == "Queue";
    public bool IsSettingsSectionSelected => SelectedSection == "Settings";

    public int VideoCount => Videos.Count;
    public int FilteredVideoCount => FilteredVideos.Count;
    public bool HasResults => Videos.Count > 0;
    public bool HasFilteredVideos => FilteredVideos.Count > 0;
    public int SelectedDownloadCount => _selectedCount;
    public bool HasSelectedDownloads => SelectedDownloadCount > 0;
    public bool HasDownloaderActivity => HasSearched || HasResults;
    public bool ShowNoResultsState => HasSearched && !HasResults && !IsLoading && !IsSearchRunning;
    public bool IsVideoEmpty => HasSearched && HasResults && !IsVideoLoaded && !IsLoading;
    public bool IsBackgroundSearchRunning => IsSearchRunning && !IsLoading;
    public string AddToQueueLabel => SelectedDownloadCount > 1 ? $"Add {SelectedDownloadCount} to Queue" : "Add to Queue";

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

    [ObservableProperty]
    private string _selectedGlobalQuality = VideoDownloaderService.HighestMp3Preference;

    public string SelectedGlobalQualitySummary => SelectedGlobalQuality switch
    {
        VideoDownloaderService.HighestMp3Preference => "Applies to every selected item. Converts the best audio to a 320 kbps MP3.",
        VideoDownloaderService.BestVideoPreference => "Applies to every selected item. Merges the highest resolution available (up to 4K/8K) with the best audio.",
        VideoDownloaderService.NativeVideoPreference => "Applies to every selected item. Saves YouTube's combined stream without re-muxing when it exists.",
        VideoDownloaderService.Video1080pPreference => "Applies to every selected item. Uses 1080p, or the closest lower resolution.",
        VideoDownloaderService.Video720pPreference => "Applies to every selected item. Uses 720p, or the closest lower resolution.",
        _ => "Applies this choice to every selected item."
    };

    public string SelectedGlobalOutputType => SelectedGlobalQuality switch
    {
        _ when VideoDownloaderService.IsAudioPreference(SelectedGlobalQuality) => "MP3 audio",
        VideoDownloaderService.BestVideoPreference => "MP4 video (WebM when the top resolution is VP9 only)",
        _ => "MP4 video"
    };

    private void UpdateQueueHeader()
    {
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
    private bool _isSearchRunning;

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
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isQuitConfirmationVisible;

    public string CurrentResultKindLabel => CurrentResultKind switch
    {
        QueryResultKind.Video => "Video",
        QueryResultKind.Playlist => "Playlist",
        QueryResultKind.Channel => "Channel",
        QueryResultKind.Search => "Search",
        _ => "Results"
    };

    public bool IsAudioMetadataEditorVisible => IsVideoLoaded && VideoDownloaderService.IsAudioPreference(SelectedGlobalQuality);

    public string QuitConfirmationMessage => ActiveDownloadCount == 1
        ? "1 download is still in progress. It will be paused and can be resumed the next time you open the app."
        : $"{ActiveDownloadCount} downloads are still in progress. They will be paused and can be resumed the next time you open the app.";

    /// <summary>
    /// Set by the view; closes the main window once the user confirmed quitting.
    /// </summary>
    public Action? RequestCloseAction { get; set; }

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
        OnPropertyChanged(nameof(AddToQueueLabel));
    }

    private void OnVideosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _resolvedVideoIds.Clear();
            foreach (var item in Videos)
            {
                _resolvedVideoIds.Add(item.Video.Id.Value);
            }

            _selectedCount = Videos.Count(v => v.IsSelected);
        }

        if (e.NewItems != null)
        {
            foreach (SelectableVideoViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnSelectableVideoPropertyChanged;
                if (item.IsSelected)
                {
                    _selectedCount++;
                }
            }
        }

        if (e.OldItems != null)
        {
            foreach (SelectableVideoViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnSelectableVideoPropertyChanged;
                _resolvedVideoIds.Remove(item.Video.Id.Value);
                if (item.IsSelected)
                {
                    _selectedCount--;
                }
            }
        }

        RaiseResultStateChanged();
        RaiseSelectionStateChanged();
    }

    private void OnSelectableVideoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableVideoViewModel.IsSelected) && sender is SelectableVideoViewModel item)
        {
            // Kept as a running count: re-counting thousands of channel uploads on every
            // checkbox change made "select all" quadratic.
            _selectedCount = Math.Max(0, _selectedCount + (item.IsSelected ? 1 : -1));
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
        // Progress and status change many times per second while downloading. The queue
        // counters only depend on the state, and restored items restart from zero anyway, so
        // reacting to progress ticks only burned UI time and rewrote the queue file constantly.
        if (e.PropertyName is nameof(DownloadTaskViewModel.State)
            or nameof(DownloadTaskViewModel.IsCompleted)
            or nameof(DownloadTaskViewModel.HasError))
        {
            RaiseQueueStateChanged();
        }

        if (e.PropertyName is nameof(DownloadTaskViewModel.State)
            or nameof(DownloadTaskViewModel.FilePath)
            or nameof(DownloadTaskViewModel.FormatSummary)
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
        OnPropertyChanged(nameof(QuitConfirmationMessage));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoResultsState));
        OnPropertyChanged(nameof(IsVideoEmpty));
        OnPropertyChanged(nameof(IsBackgroundSearchRunning));
    }

    partial void OnIsSearchRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoResultsState));
        OnPropertyChanged(nameof(IsBackgroundSearchRunning));
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

        var wasProcessing = item.IsProcessing;
        item.RequestCancel();
        item.MarkCanceled();

        // A running item deletes its own partial file once the download loop unwinds.
        if (!wasProcessing)
        {
            DeletePartialOutput(item);
        }

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

        if (!item.IsCompleted && !item.IsProcessing)
        {
            DeletePartialOutput(item);
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
        CancelActiveSearch();

        SearchQuery = string.Empty;
        Videos.Clear();
        FilteredVideos.Clear();
        PlaylistSearchQuery = string.Empty;
        IsAllSelected = false;
        SelectedVideo = null;
        ClearSelectedVideoDetails();
        IsPlaylistLoaded = false;
        CurrentResultKind = QueryResultKind.Video;
        CurrentCollectionTitle = string.Empty;
        HasSearched = false;
        IsLoading = false;
        StatusMessage = "Ready";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsSaveDebounce?.Cancel();
        StatusMessage = SettingsManager.SaveSettings(AppSettings)
            ? "Settings saved."
            : "Could not save settings. Check that your user folder is writable.";
    }

    private void OnAppSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.AppSettings.ConcurrentDownloads))
        {
            _downloadSlots.SetLimit(AppSettings.ConcurrentDownloads);
        }

        // Settings apply immediately, so persist them too instead of silently losing them
        // when the user forgets to press Save.
        _settingsSaveDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _settingsSaveDebounce = cts;
        var settings = AppSettings;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, cts.Token);
                SettingsManager.SaveSettings(settings);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    partial void OnAppSettingsChanged(AppSettings? oldValue, AppSettings newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= OnAppSettingsPropertyChanged;
        }

        newValue.PropertyChanged += OnAppSettingsPropertyChanged;
        _downloadSlots?.SetLimit(newValue.ConcurrentDownloads);
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
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!IsDirectoryWritable(path))
            {
                StatusMessage = $"Can't write to '{path}'. Choose another folder.";
                return;
            }

            AppSettings.DownloadPath = path;
            StatusMessage = "Download path updated.";
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

    /// <summary>
    /// Handles text dropped onto the window (a link dragged from a browser, for example).
    /// </summary>
    public void SearchDroppedText(string text)
    {
        var firstLine = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return;
        }

        SelectedSection = "Downloader";
        SearchQuery = firstLine;
        SearchCommand.Execute(null);
    }

    [RelayCommand]
    private void CancelSearch()
    {
        if (!IsSearchRunning)
        {
            return;
        }

        CancelActiveSearch();
        IsLoading = false;
        StatusMessage = Videos.Count > 0
            ? $"Stopped loading. {Videos.Count} result(s) kept."
            : "Search canceled.";
    }

    private void CancelActiveSearch()
    {
        var cts = Interlocked.Exchange(ref _searchCts, null);
        if (cts == null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        IsSearchRunning = false;
    }

    // Concurrent executions are allowed so a new search (or a dropped link) can replace one
    // that is still paging through a large channel instead of being ignored.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchAsync()
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        CancelActiveSearch();
        var searchCts = new CancellationTokenSource();
        _searchCts = searchCts;
        var token = searchCts.Token;

        HasSearched = true;
        IsLoading = true;
        IsSearchRunning = true;
        SelectedVideo = null;
        IsVideoLoaded = false;
        IsPlaylistLoaded = false;
        Videos.Clear();
        FilteredVideos.Clear();
        PlaylistSearchQuery = string.Empty;
        IsAllSelected = false;
        ClearSelectedVideoDetails();
        CurrentResultKind = QueryResultKind.Video;
        CurrentCollectionTitle = string.Empty;
        StatusMessage = "Searching YouTube...";

        try
        {
            var result = await _queryResolver.ResolveAsync(
                query,
                async item =>
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

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
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            StatusMessage = message;
                        }
                    });
                },
                token);

            token.ThrowIfCancellationRequested();

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
                if (SelectedVideo == null && FilteredVideos.Any()) SelectedVideo = FilteredVideos.First();
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replaced by a newer search or stopped by the user; they already updated the UI.
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusMessage = "Error: " + GetFriendlyQueryError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, searchCts))
            {
                _searchCts = null;
                IsLoading = false;
                IsSearchRunning = false;
            }

            searchCts.Dispose();
        }
    }

    private void AddResolvedVideo(IVideo video, QueryResultKind kind, string collectionTitle, int index)
    {
        if (!_resolvedVideoIds.Add(video.Id.Value))
        {
            return;
        }

        var vm = new SelectableVideoViewModel(video, kind, collectionTitle, index);
        Videos.Add(vm);
        if (MatchesFilter(vm, PlaylistSearchQuery))
        {
            FilteredVideos.Add(vm);
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
        VideoDuration = currentVideo.Duration?.ToString(@"hh\:mm\:ss") ?? "Live";
        ThumbnailUrl = value.ThumbnailUrl;

        IsVideoLoaded = true;
        StatusMessage = "Choose one download option. It applies to every selected item.";
        OnPropertyChanged(nameof(IsAudioMetadataEditorVisible));
    }

    private void ClearSelectedVideoDetails()
    {
        VideoTitle = string.Empty;
        VideoAuthor = string.Empty;
        VideoDuration = string.Empty;
        ThumbnailUrl = string.Empty;
        IsVideoLoaded = false;
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

        if (!EnsureDownloadFolderIsUsable(out var folderError))
        {
            StatusMessage = folderError;
            return;
        }

        var added = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var vid in toDownload)
        {
            if (IsAlreadyQueued(vid.Video.Url, SelectedGlobalQuality))
            {
                skipped++;
                continue;
            }

            try
            {
                EnqueueDownload(vid, SelectedGlobalQuality);
                added++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Error($"Enqueue '{vid.Title}'", ex);
            }
        }

        var parts = new List<string>();
        if (added > 0) parts.Add(added == 1 ? "Added 1 item to the queue" : $"Added {added} items to the queue");
        if (skipped > 0) parts.Add($"{skipped} already queued");
        if (failed > 0) parts.Add($"{failed} could not be added (see the log)");
        StatusMessage = parts.Count > 0 ? string.Join(", ", parts) + "." : "Nothing to add.";
    }

    private bool IsAlreadyQueued(string videoUrl, string qualityPreference)
    {
        return DownloadQueue.Any(q =>
            q.State is DownloadTaskState.Pending or DownloadTaskState.Running or DownloadTaskState.Paused
            && string.Equals(q.VideoUrl, videoUrl, StringComparison.Ordinal)
            && string.Equals(q.QualityLabel, qualityPreference, StringComparison.Ordinal));
    }

    private bool EnsureDownloadFolderIsUsable(out string error)
    {
        error = string.Empty;
        var path = AppSettings.DownloadPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose a download folder in Settings first.";
            return false;
        }

        if (!IsDirectoryWritable(path))
        {
            error = $"Can't write to the download folder '{path}'. Reconnect the drive or choose another folder in Settings.";
            return false;
        }

        return true;
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnqueueDownload(SelectableVideoViewModel item, string qualityPreference)
    {
        var video = item.Video;
        var safeTitle = FileNameSanitizer.Sanitize(video.Title);
        var isAudioOnly = VideoDownloaderService.IsAudioPreference(qualityPreference);
        var ext = isAudioOnly ? ".mp3" : ".mp4";
        var outputDirectory = AppSettings.DownloadPath;

        if (item.SourceKind is QueryResultKind.Playlist or QueryResultKind.Channel)
        {
            var folderName = FileNameSanitizer.Sanitize(
                string.IsNullOrWhiteSpace(item.CollectionTitle) ? item.SourceKind.ToString() : item.CollectionTitle,
                maxLength: 80);
            outputDirectory = Path.Combine(outputDirectory, folderName);
        }

        var orderedTitle = item.CollectionIndex > 0
                           && item.SourceKind is (QueryResultKind.Playlist or QueryResultKind.Channel)
            ? $"{item.CollectionIndex:000} - {safeTitle}"
            : safeTitle;

        var filePath = GetUniqueOutputPath(outputDirectory, $"{orderedTitle}{ext}");

        var thumbnailUrl = item.ThumbnailUrl;

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
        IDisposable? slot = null;
        var token = taskVm.CancellationTokenSource.Token;

        try
        {
            if (taskVm.State == DownloadTaskState.Pending)
            {
                taskVm.Status = "Queued";
            }

            slot = await _downloadSlots.AcquireAsync(token);

            if (taskVm.State is DownloadTaskState.Paused or DownloadTaskState.Canceled
                || token.IsCancellationRequested)
            {
                return;
            }

            taskVm.MarkRunning();

            for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
            {
                try
                {
                    await RunDownloadAttemptAsync(taskVm, taskVm.VideoUrl, taskVm.QualityLabel, attempt, token);
                    return;
                }
                catch (OperationCanceledException) when (taskVm.IsPauseRequested || taskVm.IsCancelRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxDownloadAttempts && IsTransientDownloadException(ex))
                {
                    taskVm.Progress = 0;
                    taskVm.Status = "Network slowed down. Retrying...";

                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
        }
        catch (OperationCanceledException) when (taskVm.IsPauseRequested)
        {
            taskVm.MarkPaused();
        }
        catch (OperationCanceledException) when (taskVm.IsCancelRequested)
        {
            taskVm.MarkCanceled();
            DeletePartialOutput(taskVm);
        }
        catch (Exception ex)
        {
            if (taskVm.IsPauseRequested)
            {
                taskVm.MarkPaused();
            }
            else if (taskVm.IsCancelRequested)
            {
                taskVm.MarkCanceled();
                DeletePartialOutput(taskVm);
            }
            else
            {
                AppLog.Error($"Download '{taskVm.Title}' ({taskVm.VideoUrl})", ex);
                taskVm.MarkFailed(GetFriendlyDownloadError(ex));
                DeletePartialOutput(taskVm);
            }
        }
        finally
        {
            slot?.Dispose();
            taskVm.EndProcessing();
            UpdateQueueHeader();
            ScheduleSaveQueueState();
        }
    }

    private async Task RunDownloadAttemptAsync(
        DownloadTaskViewModel taskVm,
        string videoUrl,
        string qualityPreference,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt > 1)
        {
            taskVm.MarkRunning($"Retrying download ({attempt}/{MaxDownloadAttempts})...");
        }
        else
        {
            taskVm.MarkRunning("Finding the best streams...");
        }

        var targetQuality = await _videoDownloader.ResolveStreamsAsync(videoUrl, qualityPreference, cancellationToken);
        taskVm.FormatSummary = targetQuality.DisplayName;

        // "Best" can resolve to a VP9-only WebM stream; keep the streams' native container so
        // FFmpeg can copy them instead of spending minutes re-encoding to MP4.
        if (!taskVm.IsAudio
            && !string.Equals(Path.GetExtension(taskVm.FilePath), targetQuality.OutputExtension, StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(taskVm.FilePath) ?? AppSettings.DownloadPath;
            var fileName = Path.GetFileNameWithoutExtension(taskVm.FilePath) + targetQuality.OutputExtension;
            taskVm.FilePath = GetUniqueOutputPath(directory, fileName, taskVm);
        }

        var warnings = new List<string>();
        var wantsSubtitles = AppSettings.DownloadSubtitles && !taskVm.IsAudio;
        var embedSubtitles = wantsSubtitles && AppSettings.EmbedSubtitles;
        ClosedCaptionTrackInfo? subtitleTrack = null;

        if (wantsSubtitles)
        {
            taskVm.Status = "Looking for subtitles...";
            subtitleTrack = await _videoDownloader.FindSubtitleTrackAsync(videoUrl, AppSettings.SubtitleLanguage, cancellationToken);
            if (subtitleTrack == null)
            {
                warnings.Add($"no '{AppSettings.SubtitleLanguage}' subtitles on YouTube");
            }
        }

        if (targetQuality.RequiresFfmpeg || (embedSubtitles && subtitleTrack != null))
        {
            taskVm.Status = "Preparing FFmpeg...";
            await FfmpegBootstrapper.EnsureAvailableAsync(
                message => Dispatcher.UIThread.Post(() => taskVm.Status = message),
                cancellationToken);
        }

        var progress = CreateProgressReporter(taskVm, targetQuality.TotalBytes);
        taskVm.Status = "Downloading...";

        var embeddedTracks = embedSubtitles && subtitleTrack != null
            ? new[] { subtitleTrack }
            : null;

        try
        {
            await _videoDownloader.DownloadAsync(targetQuality, taskVm.FilePath, embeddedTracks, progress, cancellationToken);
        }
        catch (Exception ex) when (embeddedTracks != null
                                   && ex is not OperationCanceledException
                                   && !IsTransientDownloadException(ex))
        {
            // A subtitle track FFmpeg cannot mux should not cost the user the whole video.
            AppLog.Error($"Embedding subtitles for '{taskVm.Title}'", ex);
            warnings.Add("subtitles could not be embedded");
            embeddedTracks = null;
            subtitleTrack = null;
            taskVm.Progress = 0;
            taskVm.Status = "Retrying without embedded subtitles...";
            await _videoDownloader.DownloadAsync(targetQuality, taskVm.FilePath, null, progress, cancellationToken);
        }

        if (subtitleTrack != null && embeddedTracks == null)
        {
            taskVm.Status = "Downloading subtitles...";
            var srtPath = Path.ChangeExtension(taskVm.FilePath, ".srt");
            var downloadedSubtitles = await _videoDownloader.DownloadSubtitlesAsync(subtitleTrack, srtPath, cancellationToken);
            if (downloadedSubtitles == null)
            {
                warnings.Add("subtitles could not be saved");
            }
        }

        if (taskVm.IsAudio && AppSettings.AutoTagAudio)
        {
            taskVm.Status = "Writing Apple Music-ready metadata...";
            try
            {
                if (string.IsNullOrWhiteSpace(taskVm.Metadata.Artist)
                    || string.IsNullOrWhiteSpace(taskVm.Metadata.ArtworkUrl))
                {
                    var videoInfo = await _videoDownloader.GetVideoDetailsAsync(videoUrl, cancellationToken);
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
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"Tagging '{taskVm.FilePath}'", ex);
                warnings.Add("metadata warning: " + ex.Message);
            }
        }

        taskVm.MarkCompleted(warnings.Count == 0
            ? "Completed"
            : "Completed · " + string.Join(" · ", warnings));
        UpdateQueueHeader();
    }

    private static IProgress<double> CreateProgressReporter(DownloadTaskViewModel taskVm, long totalBytes)
    {
        var lastUiUpdate = DateTime.MinValue;
        var lastSampleTime = DateTime.UtcNow;
        double lastSampleProgress = 0;
        double smoothedBytesPerSecond = 0;

        // Progress<T> captures the UI thread's context, so the callback already runs there.
        return new Progress<double>(p =>
        {
            var now = DateTime.UtcNow;

            var sampleSeconds = (now - lastSampleTime).TotalSeconds;
            if (sampleSeconds >= 0.5 && totalBytes > 0)
            {
                var bytesPerSecond = Math.Max(0, (p - lastSampleProgress) * totalBytes / sampleSeconds);
                smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                    ? bytesPerSecond
                    : smoothedBytesPerSecond * 0.7 + bytesPerSecond * 0.3;
                lastSampleTime = now;
                lastSampleProgress = p;
            }

            // YoutubeExplode reports every buffer read; refreshing the UI a few times per second
            // is plenty and keeps large queues responsive.
            if ((now - lastUiUpdate).TotalMilliseconds < 150 && p < 1)
            {
                return;
            }

            lastUiUpdate = now;
            taskVm.Progress = Math.Clamp(p * 100, 0, 100);

            if (p >= 0.92 && taskVm.IsAudio)
            {
                taskVm.Status = $"Converting to MP3... {taskVm.Progress:F0}%";
                return;
            }

            var status = $"Downloading: {taskVm.Progress:F1}%";
            if (smoothedBytesPerSecond > 0)
            {
                status += $" · {FormatBytes(smoothedBytesPerSecond)}/s";

                var remainingSeconds = (1 - p) * totalBytes / smoothedBytesPerSecond;
                if (remainingSeconds is > 1 and < 60 * 60 * 24)
                {
                    status += $" · {FormatDuration(TimeSpan.FromSeconds(remainingSeconds))} left";
                }
            }

            if (totalBytes > 0)
            {
                status += $" · {FormatBytes(totalBytes)}";
            }

            taskVm.Status = status;
        });
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:F0} {units[unit]}" : $"{bytes:F1} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static void DeletePartialOutput(DownloadTaskViewModel taskVm)
    {
        if (taskVm.IsCompleted || string.IsNullOrWhiteSpace(taskVm.FilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(taskVm.FilePath))
            {
                File.Delete(taskVm.FilePath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not delete partial download '{taskVm.FilePath}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelQuit()
    {
        IsQuitConfirmationVisible = false;
    }

    [RelayCommand]
    private void ConfirmQuit()
    {
        _isQuitConfirmed = true;
        IsQuitConfirmationVisible = false;
        RequestCloseAction?.Invoke();
    }

    /// <summary>
    /// Called by the window before it closes. Returns false (and shows a confirmation) when
    /// downloads are still running and the user has not confirmed quitting yet.
    /// </summary>
    public bool CanCloseNow(bool isOsShutdown)
    {
        if (_isQuitConfirmed || isOsShutdown || !HasActiveDownloads)
        {
            return true;
        }

        OnPropertyChanged(nameof(QuitConfirmationMessage));
        IsQuitConfirmationVisible = true;
        return false;
    }

    /// <summary>
    /// Stops background work and flushes state synchronously; the process is about to exit, so
    /// debounced background saves would otherwise be lost.
    /// </summary>
    public void PrepareForShutdown()
    {
        CancelActiveSearch();
        _queueSaveDebounce?.Cancel();
        _settingsSaveDebounce?.Cancel();

        foreach (var item in DownloadQueue.Where(q => q.State is DownloadTaskState.Pending or DownloadTaskState.Running))
        {
            // Pausing (rather than canceling) keeps the item resumable after restart and kills
            // any FFmpeg child process synchronously.
            item.RequestPause();
        }

        try
        {
            SaveQueueSnapshot(CreateQueueSnapshot());
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving queue on exit", ex);
        }

        SettingsManager.SaveSettings(AppSettings);
    }

    private void LoadPersistedQueue()
    {
        var queueFile = File.Exists(AppPaths.QueueStateFile)
            ? AppPaths.QueueStateFile
            : AppPaths.LegacyQueueStateFile;

        if (!File.Exists(queueFile))
        {
            return;
        }

        try
        {
            _isLoadingPersistedQueue = true;
            var json = File.ReadAllText(queueFile);
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
                    restoredStatus)
                {
                    FormatSummary = item.FormatSummary ?? string.Empty
                };

                DownloadQueue.Add(taskVm);
            }

            if (DownloadQueue.Count > 0)
            {
                StatusMessage = $"Restored {DownloadQueue.Count} queue item(s). Paused items will not restart until resumed.";
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Restoring the download queue", ex);
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

        _queueSaveDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _queueSaveDebounce = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                var snapshot = await Dispatcher.UIThread.InvokeAsync(CreateQueueSnapshot);
                cts.Token.ThrowIfCancellationRequested();
                SaveQueueSnapshot(snapshot);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLog.Error("Saving the download queue", ex);
            }
        });
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
                FormatSummary = item.FormatSummary,
                Metadata = item.Metadata.ToModel()
            }).ToList()
        };
    }

    private static readonly object QueueFileLock = new();

    private static void SaveQueueSnapshot(DownloadQueueSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, QueueJsonOptions);
        lock (QueueFileLock)
        {
            AppPaths.WriteAllTextAtomic(AppPaths.QueueStateFile, json);
        }
    }

    private string GetUniqueOutputPath(string outputDirectory, string fileName, DownloadTaskViewModel? owner = null)
    {
        Directory.CreateDirectory(outputDirectory);

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(outputDirectory, fileName);
        var index = 2;

        while (File.Exists(candidate)
               || DownloadQueue.Any(q => !ReferenceEquals(q, owner)
                                         && string.Equals(q.FilePath, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = Path.Combine(outputDirectory, $"{baseName} ({index}){extension}");
            index++;
        }

        return candidate;
    }

    private static bool IsTransientDownloadException(Exception ex)
    {
        return ex is HttpRequestException
               || ex is HttpIOException
               || ex is SocketException
               || ex is TimeoutException
               || ex is TaskCanceledException
               || ex is RequestLimitExceededException
               || (ex.InnerException != null && IsTransientDownloadException(ex.InnerException));
    }

    private static string GetFriendlyDownloadError(Exception ex)
    {
        switch (ex)
        {
            case VideoRequiresPurchaseException:
                return "This video requires a purchase or rental on YouTube.";
            case VideoUnavailableException:
                return "This video is unavailable (removed, private or blocked in your region).";
            case VideoUnplayableException:
                return "YouTube won't let this video be downloaded (age-restricted, members-only or live).";
            case RequestLimitExceededException:
                return "YouTube is rate limiting requests. Wait a few minutes, then use Retry Failed.";
            case UnauthorizedAccessException:
                return "Permission denied writing to the download folder. Choose another folder in Settings.";
            case IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase):
                return "The disk is full. Free some space, then use Retry Failed.";
        }

        if (IsTransientDownloadException(ex)
            || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Network timeout while downloading. Use Retry Failed to try again.";
        }

        return "Error: " + ex.Message;
    }

    private static string GetFriendlyQueryError(Exception ex)
    {
        return ex switch
        {
            VideoUnavailableException => "That video is unavailable (removed, private or blocked in your region).",
            HttpRequestException or HttpIOException or SocketException => "Could not reach YouTube. Check your internet connection.",
            _ => ex.Message
        };
    }
}
