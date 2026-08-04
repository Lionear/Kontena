namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>
/// An opened folder — Plan §2: no import, no database, the files on disk are the truth. Opening is
/// picking a folder or cloning a repo; a <c>kustomization.yaml</c> anywhere under it makes it a
/// Kustomize project. That is a fact read from the files, never guessed from a directory name.
/// </summary>
public sealed class ManifestWorkspace
{
    private ManifestWorkspace(string rootPath, WorkspaceFolder root, bool isKustomizeProject)
    {
        RootPath = rootPath;
        Root = root;
        IsKustomizeProject = isKustomizeProject;
    }

    public string RootPath { get; }
    public WorkspaceFolder Root { get; }
    public bool IsKustomizeProject { get; }

    public static ManifestWorkspace Open(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"No such workspace folder: {fullPath}");

        var root = BuildFolder(fullPath, Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)));
        return new ManifestWorkspace(fullPath, root, ContainsKustomization(root));
    }

    // ponytail: eager, unbounded recursive scan — fine for a manifests folder, a multi-GB cloned
    // monorepo will stall the open. Add lazy/on-expand loading if that turns out to matter.
    private static WorkspaceFolder BuildFolder(string path, string name)
    {
        var children = new List<WorkspaceNode>();

        foreach (var dir in Directory.EnumerateDirectories(path).Order(StringComparer.OrdinalIgnoreCase))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName == ".git")
                continue; // Git's own plumbing, never a manifest concern.

            children.Add(BuildFolder(dir, dirName));
        }

        foreach (var file in Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase))
            children.Add(new WorkspaceFile(Path.GetFileName(file), file));

        return new WorkspaceFolder(name, path, children);
    }

    private static bool ContainsKustomization(WorkspaceNode node) => node switch
    {
        WorkspaceFile file => file.Name.Equals("kustomization.yaml", StringComparison.OrdinalIgnoreCase)
            || file.Name.Equals("kustomization.yml", StringComparison.OrdinalIgnoreCase),
        WorkspaceFolder folder => folder.Children.Any(ContainsKustomization),
        _ => false,
    };
}
