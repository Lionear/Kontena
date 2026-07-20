using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kontena.App.ViewModels;

namespace Kontena.App.Views;

public partial class BuildImageView : UserControl
{
    private INotifyCollectionChanged? _console;

    public BuildImageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_console is not null)
            _console.CollectionChanged -= OnConsoleChanged;

        _console = (DataContext as BuildImageViewModel)?.Console;

        if (_console is not null)
            _console.CollectionChanged += OnConsoleChanged;
    }

    private void OnConsoleChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        if (DataContext is not BuildImageViewModel vm || vm.Console.Count == 0)
            return;

        // Tail-follow the build console after layout.
        Dispatcher.UIThread.Post(
            () => this.FindControl<ListBox>("ConsoleList")?.ScrollIntoView(vm.Console.Count - 1),
            DispatcherPriority.Background);
    }
}
