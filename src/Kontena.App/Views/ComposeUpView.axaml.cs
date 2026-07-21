using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class ComposeUpView : UserControl
{
    private INotifyCollectionChanged? _console;

    public ComposeUpView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

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

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_console is not null)
            _console.CollectionChanged -= OnConsoleChanged;

        _console = (DataContext as ComposeUpViewModel)?.Console;

        if (_console is not null)
            _console.CollectionChanged += OnConsoleChanged;
    }

    private void OnConsoleChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        if (DataContext is not ComposeUpViewModel vm || vm.Console.Count == 0)
            return;

        // Tail-follow the console after layout.
        Dispatcher.UIThread.Post(
            () => this.FindControl<ListBox>("ConsoleList")?.ScrollIntoView(vm.Console.Count - 1),
            DispatcherPriority.Background);
    }
}
