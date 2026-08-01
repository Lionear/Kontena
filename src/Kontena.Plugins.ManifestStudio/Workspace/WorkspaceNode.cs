namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>
/// One entry in the workspace tree. <c>Children</c> defaults to empty rather than being abstract so a
/// TreeView-style binding can point at it uniformly for files and folders alike — a leaf simply offers
/// nothing to expand.
/// </summary>
public abstract class WorkspaceNode(string name, string fullPath)
{
    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public virtual IReadOnlyList<WorkspaceNode> Children => [];
}

public sealed class WorkspaceFile(string name, string fullPath) : WorkspaceNode(name, fullPath);

public sealed class WorkspaceFolder(string name, string fullPath, IReadOnlyList<WorkspaceNode> children)
    : WorkspaceNode(name, fullPath)
{
    public override IReadOnlyList<WorkspaceNode> Children { get; } = children;
}
