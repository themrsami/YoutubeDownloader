using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PremiumYoutubeDownloader.ViewModels;

namespace PremiumYoutubeDownloader.Views;

public partial class MainWindow : Window
{
    private const double WindowsCaptionButtonSafeWidth = 150;

    public MainWindow()
    {
        InitializeComponent();
        ApplyPlatformChromeSafeArea();

        Loaded += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSection = "Downloader";
            }
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, DropHandler);
    }

    private void ApplyPlatformChromeSafeArea()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Windows keeps three caption buttons over the extended titlebar's top-right edge.
        TitleBarChrome.Padding = new Thickness(14, 7, WindowsCaptionButtonSafeWidth, 7);
    }

    private void DropHandler(object? sender, DragEventArgs e)
    {
        // Drag and drop API changed in Avalonia 12.0
        // We will handle it manually or wait for stable documentation
    }
}
