namespace Kontena.Plugins.ManifestStudio.Git;

/// <summary>One changed path, the way <c>git status</c> reports it — staged vs. unstaged is not
/// distinguished (Plan §7: no per-hunk staging), only what actually changed.</summary>
public sealed record GitFileChange(string Path, string Status);

/// <summary>
/// The state of a workspace's repository. <c>Ahead</c>/<c>Behind</c> come from the porcelain branch
/// header — being behind the remote is a warning, never a block (Plan §7): the workspace's job is to
/// let you keep writing, not to force a pull first.
/// </summary>
public sealed record GitStatus(string Branch, int Ahead, int Behind, IReadOnlyList<GitFileChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;
}
