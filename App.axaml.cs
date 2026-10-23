using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PremiumYoutubeDownloader.Services;
using PremiumYoutubeDownloader.ViewModels;
using PremiumYoutubeDownloader.Views;

namespace PremiumYoutubeDownloader;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = vm };
            _viewModel = vm;
            _mainWindow = mainWindow;

            // Keep the app alive after an unexpected UI exception: losing the whole download
            // queue to one bad callback is worse than showing the error.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLog.Error("Unhandled UI exception", e.Exception);
                vm.StatusMessage = "Unexpected error: " + e.Exception.Message;
                e.Handled = true;
            };

            vm.SelectDownloadFolderAction = async () =>
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
                if (topLevel == null) return null;

                IStorageFolder? startLocation = null;
                try
                {
                    startLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(vm.AppSettings.DownloadPath);
                }
                catch
                {
                    // The saved folder may be on a drive that is no longer connected.
                }

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Download Folder",
                    AllowMultiple = false,
                    SuggestedStartLocation = startLocation
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };

            vm.SelectArtworkFileAction = async () =>
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
                if (topLevel == null) return null;

                var imageType = new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
                    MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" },
                    AppleUniformTypeIdentifiers = new[] { "public.jpeg", "public.png", "org.webmproject.webp" }
                };

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Cover Artwork",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { imageType }
                });

                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };

            desktop.MainWindow = mainWindow;
            Dispatcher.UIThread.Post(() => vm.SelectedSection = "Downloader", DispatcherPriority.Loaded);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnSettingsMenuItemClick(object? sender, EventArgs e)
    {
        ShowSection("Settings");
    }

    private void OnQueueMenuItemClick(object? sender, EventArgs e)
    {
        ShowSection("Queue");
    }

    private void ShowSection(string section)
    {
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.SelectedSection = section;
        _mainWindow?.Activate();
    }
}
