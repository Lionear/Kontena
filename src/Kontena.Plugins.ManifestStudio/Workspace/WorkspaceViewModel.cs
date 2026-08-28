using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Plugins.ManifestStudio.Git;

namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>Owns the open tabs for one <see cref="ManifestWorkspace"/>. Opening a path twice reuses
/// its existing tab instead of duplicating it — two tabs on the same file would let a save from one
/// silently discard edits made through the other.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public WorkspaceViewModel(ManifestWorkspace workspace)
    {
        Workspace = workspace;
        Rows = TreeRow.Flatten(workspace.Root);
    }

    public ManifestWorkspace Workspace { get; }
    public ObservableCollection<OpenDocument> OpenTabs { get; } = [];

    /// <summary>The file pane's rows — see <see cref="TreeRow"/> for why the pane is a flat list.</summary>
    public IReadOnlyList<TreeRow> Rows { get; }

    /// <summary>The workspace folder's own name, for the breadcrumb.</summary>
    public string Name => Workspace.Root.Name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveActiveCommand))]
    [NotifyPropertyChangedFor(nameof(ActivePath))]
    private OpenDocument? _activeDocument;

    /// <summary>The active document's path relative to the workspace root, in the forward-slash form the
    /// breadcrumb and the tabs both read better in. Empty when nothing is open.</summary>
    public string ActivePath => ActiveDocument is { } document
        ? Path.GetRelativePath(Workspace.RootPath, document.Path).Replace('\\', '/')
        : string.Empty;

    /// <summary>Pushes <c>git status</c> onto the file pane so the tree carries the same M/A badges the
    /// Source control page lists (KON-427). Null clears them: a folder that is not a repository, or a
    /// <c>git</c> that could not answer, shows no badges rather than stale ones.</summary>
    public void SetGitStatus(GitStatus? status)
    {
        foreach (var row in Rows)
        {
            if (status is null)
                row.GitStatus = null;
            else
                row.ApplyGitStatus(Workspace.RootPath, status.Changes);
        }
    }

    partial void OnActiveDocumentChanged(OpenDocument? value)
    {
        foreach (var row in Rows)
            row.IsActive = value is not null && row.Node.FullPath == value.Path;

        foreach (var tab in OpenTabs)
            tab.IsActive = ReferenceEquals(tab, value);
    }

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
