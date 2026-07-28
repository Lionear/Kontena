using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class BuildImageView : UserControl
{
    // Tail-following lives on the list itself — see Behaviors/AutoScroll.cs (KON-165).
    public BuildImageView() => InitializeComponent();

    private async void OnBrowseDockerfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BuildImageViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Dockerfile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Dockerfiles") { Patterns = ["Dockerfile", "Containerfile", "*.Dockerfile", "Dockerfile.*"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
            vm.SetDockerfile(path);
    }

    private async void OnBrowseContextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BuildImageViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the build context",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
            vm.ContextPath = path;
    }

}
