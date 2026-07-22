using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ApplyManifestView : UserControl
{
    public ApplyManifestView() => InitializeComponent();

    private async void OnBrowseKustomizeClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Choose the overlay directory") is { } path && DataContext is ApplyManifestViewModel vm)
            vm.SetKustomizePath(path);
    }

    private async void OnBrowseChartClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Choose the chart directory") is { } path && DataContext is ApplyManifestViewModel vm)
            vm.SetChartPath(path);
    }

    private async void OnAddValuesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplyManifestViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose values files",

            // Several at once, because a render usually stacks a base file and an environment file.
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Values files") { Patterns = ["*.yaml", "*.yml"] },
                FilePickerFileTypes.All,
            ],
        });

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { Length: > 0 } path)
                vm.AddValuesFile(path);
        }
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
