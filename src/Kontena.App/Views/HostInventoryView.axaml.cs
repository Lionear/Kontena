using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class HostInventoryView : UserControl
{
    public HostInventoryView() => InitializeComponent();

    /// <summary>
    /// Picks a k0sctl.yaml and hands its text to the view model. Reading the file is the view's job
    /// here, as it is on the other pages that browse: the picker needs a TopLevel, and the view model
    /// stays testable by taking the text rather than the path.
    /// </summary>
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HostInventoryViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a k0sctl.yaml",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("k0sctl config") { Patterns = ["k0sctl.yaml", "*.yaml", "*.yml"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is not { Length: > 0 } path)
            return;

        try
        {
            vm.ImportK0sctl(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable file is an answer, not a crash — the same treatment the tooling checks get.
            vm.ImportMessage = $"Could not read that file: {ex.Message}";
        }
    }
}
