using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EnneaOdin.ViewModels;

namespace EnneaOdin.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel VM => (MainWindowViewModel)DataContext!;

    // ── File picker filter for Samsung firmware files ─────────────────────────
    private static readonly FilePickerFileType FirmwareFilter = new("Samsung Firmware")
    {
        Patterns = new[] { "*.tar", "*.md5", "*.tar.md5", "*.img", "*.bin", "*.lz4" },
        MimeTypes = new[] { "application/octet-stream" }
    };

    private static readonly FilePickerOpenOptions PickerOptions = new()
    {
        Title = "Select Samsung Firmware Package",
        AllowMultiple = false,
        FileTypeFilter = new[] { FirmwareFilter, FilePickerFileTypes.All }
    };

    private async Task<string?> PickFileAsync()
    {
        var result = await StorageProvider.OpenFilePickerAsync(PickerOptions);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private async void OnBrowseBL(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path != null) VM.SlotBL = path;
    }

    private async void OnBrowseAP(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path != null) VM.SlotAP = path;
    }

    private async void OnBrowseCP(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path != null) VM.SlotCP = path;
    }

    private async void OnBrowseCSC(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path != null) VM.SlotCSC = path;
    }

    private async void OnBrowseUserData(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync();
        if (path != null) VM.SlotUserData = path;
    }
}
