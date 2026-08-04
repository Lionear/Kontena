using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>Owns the open tabs for one <see cref="ManifestWorkspace"/>. Opening a path twice reuses
/// its existing tab instead of duplicating it — two tabs on the same file would let a save from one
/// silently discard edits made through the other.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public WorkspaceViewModel(ManifestWorkspace workspace) => Workspace = workspace;

    public ManifestWorkspace Workspace { get; }
    public ObservableCollection<OpenDocument> OpenTabs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveCommand))]
    private OpenDocument? _activeDocument;

    /// <summary>Bound from the tree's double-tap handler; a folder node is a no-op, not an error.</summary>
    [RelayCommand]
    private void OpenNode(WorkspaceNode? node)
    {
        if (node is WorkspaceFile file)
            Open(file.FullPath);
    }

    [RelayCommand]
    private void CloseTab(OpenDocument? document)
    {
        if (document is not null)
            Close(document);
    }

    [RelayCommand(CanExecute = nameof(CanSaveActive))]
    private void SaveActive() => ActiveDocument!.Save();

    private bool CanSaveActive() => ActiveDocument is not null;

    public OpenDocument Open(string path)
    {
        var existing = OpenTabs.FirstOrDefault(d => d.Path == path);
        if (existing is not null)
        {
            ActiveDocument = existing;
            return existing;
        }

        var document = OpenDocument.Load(path);
        OpenTabs.Add(document);
        ActiveDocument = document;
        return document;
    }

    public void Close(OpenDocument document)
    {
        var closingIndex = OpenTabs.IndexOf(document);
        if (closingIndex < 0)
            return;

        OpenTabs.RemoveAt(closingIndex);

        if (!ReferenceEquals(ActiveDocument, document))
            return;

        ActiveDocument = OpenTabs.Count == 0 ? null : OpenTabs[Math.Min(closingIndex, OpenTabs.Count - 1)];
    }
}
