using System;
using System.Linq;
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
        AddPlatformKeyBindings();

        Loaded += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SelectedSection = "Downloader";
            }
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, DragOverHandler);
        AddHandler(DragDrop.DropEvent, DropHandler);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.RequestCloseAction = Close;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (!vm.CanCloseNow(e.CloseReason == WindowCloseReason.OSShutdown))
            {
                e.Cancel = true;
                return;
            }

            vm.PrepareForShutdown();
        }

        base.OnClosing(e);
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

    private void AddPlatformKeyBindings()
    {
        // Cmd+L on macOS, Ctrl+L elsewhere: jump to the search field like a browser address bar.
        var commandModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.L && e.KeyModifiers == commandModifier)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
    }

    private static string? GetDroppedText(DragEventArgs e)
    {
        var text = e.DataTransfer.TryGetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // Browsers on macOS and Windows may hand over a dragged link as a URL "file" instead of text.
        var files = e.DataTransfer.TryGetFiles();
        var link = files?
            .Select(f => f.Path)
            .FirstOrDefault(uri => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

        return link?.AbsoluteUri;
    }

    private void DragOverHandler(object? sender, DragEventArgs e)
    {
        var hasLink = e.DataTransfer.Contains(DataFormat.Text) || e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasLink ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropHandler(object? sender, DragEventArgs e)
    {
        var text = GetDroppedText(e);
        if (string.IsNullOrWhiteSpace(text) || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.SearchDroppedText(text);
        e.Handled = true;
    }
}
