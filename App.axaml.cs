using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PremiumYoutubeDownloader.ViewModels;
using PremiumYoutubeDownloader.Views;

namespace PremiumYoutubeDownloader;

public partial class App : Application
{
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
            
            vm.SelectDownloadFolderAction = async () =>
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
                if (topLevel == null) return null;
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Download Folder",
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            };

            vm.SelectArtworkFileAction = async () =>
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
                if (topLevel == null) return null;

                var imageType = new Avalonia.Platform.Storage.FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
                    MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" },
                    AppleUniformTypeIdentifiers = new[] { "public.jpeg", "public.png", "org.webmproject.webp" }
                };

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Select Cover Artwork",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { imageType }
                });

                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };

            desktop.MainWindow = mainWindow;
            Dispatcher.UIThread.Post(() => vm.SelectedSection = "Downloader", DispatcherPriority.Loaded);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
