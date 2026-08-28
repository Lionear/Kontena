using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Kontena.Plugins.ManifestStudio.Git;

namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>
/// One row of the file pane (KON-427). The pane is a flat, indented list rather than a
/// <c>TreeView</c>, because that is what the mockup draws: no chevrons, every folder open, indentation
/// carrying the nesting. It also keeps the row's look out of a control theme's internals — the
/// selected/hover state is a class on a <c>Border</c> this plugin owns, not a template part named by
/// whichever Fluent version happens to be referenced.
/// </summary>
public sealed partial class TreeRow : ObservableObject
{
    /// <summary>15px per level, matching the mockup's <c>.lvl1</c>/<c>.lvl2</c> — deep enough to read as
    /// nesting at 12.5px, shallow enough that four levels still leave room for a filename.</summary>
    private const double IndentPerLevel = 15;

    public TreeRow(WorkspaceNode node, int depth)
    {
        Node = node;
        Indent = new Thickness(depth * IndentPerLevel, 0, 0, 0);
    }

    public WorkspaceNode Node { get; }
    public string Name => Node.Name;
    public bool IsFolder => Node is WorkspaceFolder;
    public Thickness Indent { get; }

    /// <summary>The document open in the editor right now, which is a different question from which row
    /// the pointer is over — hover is the control's, this is the workspace's.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The single letter git uses for this path, or null when the file is unchanged or the
    /// workspace is not a repository. Deliberately the letter and not the word: the badge sits at the
    /// end of a 216px row, and the word is in the Source control page where there is room for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    [NotifyPropertyChangedFor(nameof(IsAdded))]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    [NotifyPropertyChangedFor(nameof(IsRemoved))]
    private string? _gitStatus;

    public bool HasBadge => GitStatus is not null;
    public string Badge => GitStatus is { Length: > 0 } status ? status[..1] : string.Empty;

    public bool IsAdded => GitStatus is "Added" or "Untracked";
    public bool IsModified => GitStatus is "Modified" or "Renamed" or "Changed";
    public bool IsRemoved => GitStatus is "Deleted";

    /// <summary>Folders first, then files, each alphabetical — the order
    /// <see cref="ManifestWorkspace"/> already built the tree in, flattened without resorting it.</summary>
    public static List<TreeRow> Flatten(WorkspaceNode root)
    {
        var rows = new List<TreeRow>();
        Walk(root.Children, 0, rows);
        return rows;
    }

    private static void Walk(IReadOnlyList<WorkspaceNode> nodes, int depth, List<TreeRow> rows)
    {
        foreach (var node in nodes)
        {
            rows.Add(new TreeRow(node, depth));
            if (node is WorkspaceFolder folder)
                Walk(folder.Children, depth + 1, rows);
        }
    }

    /// <summary>Matches git's repository-relative paths against the rows' absolute ones. Git speaks
    /// forward slashes on every platform, so the row's path is normalised rather than the other way
    /// round — rewriting git's output would make the comparison depend on which way the wind blows.</summary>
    internal void ApplyGitStatus(string rootPath, IReadOnlyList<GitFileChange> changes)
    {
        if (IsFolder)
            return;

        var relative = Path.GetRelativePath(rootPath, Node.FullPath).Replace('\\', '/');
        GitStatus = changes.FirstOrDefault(c => c.Path == relative)?.Status;
    }
}
