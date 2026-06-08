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

    public StreamQualityOption QualityOption { get; }
    public CancellationTokenSource CancellationTokenSource { get; }

    public DownloadTaskViewModel(string title, StreamQualityOption qualityOption, string filePath)
    {
        Title = title;
        QualityOption = qualityOption;
        FilePath = filePath;
        CancellationTokenSource = new CancellationTokenSource();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(FilePath))
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{FilePath}\"",
                    UseShellExecute = true
                });
            }
        }
    }
}
