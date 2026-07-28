using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ComposeUpView : UserControl
{
    // Tail-following lives on the list itself — see Behaviors/AutoScroll.cs (KON-165).
    public ComposeUpView() => InitializeComponent();

    private async void OnBrowseComposeFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposeUpViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a compose file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Compose files")
                {
                    Patterns = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml", "*.yml", "*.yaml"],
                },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
            vm.SetComposeFile(path);
    }
}
