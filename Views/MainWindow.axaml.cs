using Avalonia.Controls;
using Avalonia.Input;
using PremiumYoutubeDownloader.ViewModels;

namespace PremiumYoutubeDownloader.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private void DropHandler(object? sender, DragEventArgs e)
    {
        // Drag and drop API changed in Avalonia 12.0
        // We will handle it manually or wait for stable documentation
    }
}
