using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>
/// Hosts one <see cref="WorkspaceViewModel"/>: folder tree on the left, tabs + editor on the right
/// (KON-287). The folder picker is the one piece of this view nothing here can unit-test — it needs a
/// real <see cref="TopLevel"/> storage provider, same as every other file picker in Kontena
/// (Notes.md "Handmatig testen vóór de tag").
/// </summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();
        if (path is not null)
            DataContext = new WorkspaceViewModel(ManifestWorkspace.Open(path));
    }

    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: WorkspaceNode node } && DataContext is WorkspaceViewModel vm)
            vm.OpenNodeCommand.Execute(node);
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is StyledElement { DataContext: OpenDocument document } && DataContext is WorkspaceViewModel vm)
            vm.CloseTabCommand.Execute(document);
    }
}
