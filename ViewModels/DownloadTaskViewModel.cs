using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PremiumYoutubeDownloader.Models;

namespace PremiumYoutubeDownloader.ViewModels;

public partial class DownloadTaskViewModel : ViewModelBase
{
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

    public StreamQualityOption? QualityOption { get; }
    public CancellationTokenSource CancellationTokenSource { get; private set; }

    public string FileName => Path.GetFileName(FilePath);
    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);
    public bool IsProgressVisible => !IsCompleted && !HasError;

    public DownloadTaskViewModel(
        string title,
        StreamQualityOption? qualityOption,
        string filePath,
        string thumbnailUrl = "",
        string qualityLabel = "",
        string videoUrl = "")
    {
        Title = title;
        QualityOption = qualityOption;
        FilePath = filePath;
        ThumbnailUrl = thumbnailUrl;
        QualityLabel = qualityLabel;
        VideoUrl = videoUrl;
        CancellationTokenSource = new CancellationTokenSource();
    }

    public void ResetForRetry()
    {
        try
        {
            CancellationTokenSource.Dispose();
        }
        catch { }

        CancellationTokenSource = new CancellationTokenSource();
        Progress = 0;
        IsCompleted = false;
        HasError = false;
        Status = "Queued for retry";
    }

    partial void OnFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(FileName));
    }

    partial void OnThumbnailUrlChanged(string value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
    }

    partial void OnIsCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressVisible));
    }

    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressVisible));
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
