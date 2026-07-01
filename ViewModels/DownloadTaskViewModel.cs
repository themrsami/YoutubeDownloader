using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PremiumYoutubeDownloader.Models;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class DownloadTaskViewModel : ViewModelBase
{
    private int _isProcessing;

    public string Id { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private double _progress = 0;

    [ObservableProperty]
    private string _status = "Pending";

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _isCompleted = false;

    [ObservableProperty]
    private bool _hasError = false;

    [ObservableProperty]
    private string _thumbnailUrl = string.Empty;

    [ObservableProperty]
    private string _qualityLabel = string.Empty;

    [ObservableProperty]
    private string _videoUrl = string.Empty;

    [ObservableProperty]
    private DownloadTaskState _state = DownloadTaskState.Pending;

    [ObservableProperty]
    private bool _isAudio;

    [ObservableProperty]
    private QueryResultKind _sourceKind = QueryResultKind.Video;

    [ObservableProperty]
    private string _collectionTitle = string.Empty;

    [ObservableProperty]
    private int _collectionIndex;

    public StreamQualityOption? QualityOption { get; }
    public AudioMetadataViewModel Metadata { get; }
    public CancellationTokenSource CancellationTokenSource { get; private set; }
    public bool IsPauseRequested { get; private set; }
    public bool IsCancelRequested { get; private set; }
    public bool IsProcessing => _isProcessing == 1;

    public Func<DownloadTaskViewModel, Task>? PauseRequestedAsync { get; set; }
    public Func<DownloadTaskViewModel, Task>? ResumeRequestedAsync { get; set; }
    public Func<DownloadTaskViewModel, Task>? CancelRequestedAsync { get; set; }
    public Func<DownloadTaskViewModel, Task>? RetryRequestedAsync { get; set; }
    public Func<DownloadTaskViewModel, Task>? RemoveRequestedAsync { get; set; }
    public Func<DownloadTaskViewModel, Task>? ChooseArtworkRequestedAsync { get; set; }

    public string FileName => Path.GetFileName(FilePath);
    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);
    public bool IsProgressVisible => State is DownloadTaskState.Pending or DownloadTaskState.Running or DownloadTaskState.Paused;
    public bool IsRunning => State == DownloadTaskState.Running;
    public bool IsPending => State == DownloadTaskState.Pending;
    public bool IsPaused => State == DownloadTaskState.Paused;
    public bool IsCanceled => State == DownloadTaskState.Canceled;
    public bool IsFinished => State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled;
    public bool CanPause => State is DownloadTaskState.Pending or DownloadTaskState.Running;
    public bool CanResume => State == DownloadTaskState.Paused;
    public bool CanCancel => State is DownloadTaskState.Pending or DownloadTaskState.Running or DownloadTaskState.Paused;
    public bool CanRetry => State is DownloadTaskState.Failed or DownloadTaskState.Canceled;
    public bool CanRemove => State != DownloadTaskState.Running;
    public bool CanReveal => !string.IsNullOrWhiteSpace(FilePath) && (IsCompleted || File.Exists(FilePath));

    public DownloadTaskViewModel(
        string title,
        StreamQualityOption? qualityOption,
        string filePath,
        string thumbnailUrl = "",
        string qualityLabel = "",
        string videoUrl = "",
        AudioMetadataViewModel? metadata = null,
        bool isAudio = false,
        QueryResultKind sourceKind = QueryResultKind.Video,
        string collectionTitle = "",
        int collectionIndex = 0,
        string? id = null,
        DownloadTaskState initialState = DownloadTaskState.Pending,
        double initialProgress = 0,
        string initialStatus = "Pending")
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        Title = title;
        QualityOption = qualityOption;
        FilePath = filePath;
        ThumbnailUrl = thumbnailUrl;
        QualityLabel = qualityLabel;
        VideoUrl = videoUrl;
        IsAudio = isAudio;
        SourceKind = sourceKind;
        CollectionTitle = collectionTitle;
        CollectionIndex = collectionIndex;
        Progress = initialProgress;
        Metadata = metadata ?? new AudioMetadataViewModel { Title = title, SourceUrl = videoUrl, ArtworkUrl = thumbnailUrl };
        Metadata.PropertyChanged += OnMetadataPropertyChanged;
        CancellationTokenSource = new CancellationTokenSource();
        State = initialState;
        Status = string.IsNullOrWhiteSpace(initialStatus) ? GetDefaultStatus(initialState) : initialStatus;
        SyncCompletionFlags();
    }

    public void ResetForRetry()
    {
        ReplaceCancellationTokenSource();
        IsPauseRequested = false;
        IsCancelRequested = false;
        Progress = 0;
        State = DownloadTaskState.Pending;
        Status = "Queued for retry";
    }

    public void ResetForResume()
    {
        ReplaceCancellationTokenSource();
        IsPauseRequested = false;
        IsCancelRequested = false;
        Progress = 0;
        State = DownloadTaskState.Pending;
        Status = "Queued to restart from the beginning";
    }

    public void MarkPending(string status = "Pending")
    {
        State = DownloadTaskState.Pending;
        Status = status;
    }

    public void MarkRunning(string status = "Downloading...")
    {
        IsPauseRequested = false;
        IsCancelRequested = false;
        State = DownloadTaskState.Running;
        Status = status;
    }

    public void MarkPaused(string status = "Paused. Resume restarts this item from the beginning.")
    {
        State = DownloadTaskState.Paused;
        Status = status;
    }

    public void MarkCompleted()
    {
        State = DownloadTaskState.Completed;
        Status = "Completed";
        Progress = 100;
    }

    public void MarkFailed(string status)
    {
        State = DownloadTaskState.Failed;
        Status = status;
    }

    public void MarkCanceled(string status = "Canceled")
    {
        State = DownloadTaskState.Canceled;
        Status = status;
    }

    public void RequestPause()
    {
        IsPauseRequested = true;
        IsCancelRequested = false;
        try { CancellationTokenSource.Cancel(); } catch { }
    }

    public void RequestCancel()
    {
        IsCancelRequested = true;
        IsPauseRequested = false;
        try { CancellationTokenSource.Cancel(); } catch { }
    }

    public bool TryBeginProcessing()
    {
        if (Interlocked.Exchange(ref _isProcessing, 1) == 1)
        {
            return false;
        }

        OnPropertyChanged(nameof(IsProcessing));
        RaiseStatePropertiesChanged();
        return true;
    }

    public void EndProcessing()
    {
        Interlocked.Exchange(ref _isProcessing, 0);
        OnPropertyChanged(nameof(IsProcessing));
        RaiseStatePropertiesChanged();
    }

    private void ReplaceCancellationTokenSource()
    {
        try
        {
            CancellationTokenSource.Dispose();
        }
        catch { }

        CancellationTokenSource = new CancellationTokenSource();
    }

    partial void OnFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(CanReveal));
    }

    partial void OnThumbnailUrlChanged(string value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
    }

    partial void OnIsCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(CanReveal));
    }

    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressVisible));
    }

    partial void OnStateChanged(DownloadTaskState value)
    {
        SyncCompletionFlags();
        RaiseStatePropertiesChanged();
    }

    private void SyncCompletionFlags()
    {
        IsCompleted = State == DownloadTaskState.Completed;
        HasError = State == DownloadTaskState.Failed;
    }

    private void RaiseStatePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsCanceled));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanReveal));
    }

    private void OnMetadataPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Metadata));
    }

    private static string GetDefaultStatus(DownloadTaskState state)
    {
        return state switch
        {
            DownloadTaskState.Running => "Paused after app restart. Resume restarts from the beginning.",
            DownloadTaskState.Paused => "Paused. Resume restarts this item from the beginning.",
            DownloadTaskState.Completed => "Completed",
            DownloadTaskState.Failed => "Failed",
            DownloadTaskState.Canceled => "Canceled",
            _ => "Pending"
        };
    }

    [RelayCommand]
    private async Task Pause()
    {
        if (CanPause && PauseRequestedAsync != null)
        {
            await PauseRequestedAsync(this);
        }
    }

    [RelayCommand]
    private async Task Resume()
    {
        if (CanResume && ResumeRequestedAsync != null)
        {
            await ResumeRequestedAsync(this);
        }
    }

    [RelayCommand]
    private async Task Cancel()
    {
        if (CanCancel && CancelRequestedAsync != null)
        {
            await CancelRequestedAsync(this);
        }
    }

    [RelayCommand]
    private async Task Retry()
    {
        if (CanRetry && RetryRequestedAsync != null)
        {
            await RetryRequestedAsync(this);
        }
    }

    [RelayCommand]
    private async Task Remove()
    {
        if (CanRemove && RemoveRequestedAsync != null)
        {
            await RemoveRequestedAsync(this);
        }
    }

    [RelayCommand]
    private async Task ChooseArtwork()
    {
        if (IsAudio && ChooseArtworkRequestedAsync != null)
        {
            await ChooseArtworkRequestedAsync(this);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            if (string.IsNullOrEmpty(FilePath)) return;

            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{FilePath}\"",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false
                };

                if (File.Exists(FilePath))
                {
                    processInfo.ArgumentList.Add("-R");
                    processInfo.ArgumentList.Add(FilePath);
                }
                else
                {
                    processInfo.ArgumentList.Add(dir);
                }

                Process.Start(processInfo);
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = false
                });
            }
        }
        catch
        {
            Status = "Could not open download folder.";
        }
    }
}
