namespace Kontena.Plugins.ManifestStudio.Git;

/// <summary>One changed path, the way <c>git status</c> reports it — staged vs. unstaged is not
/// distinguished (Plan §7: no per-hunk staging), only what actually changed.</summary>
public sealed record GitFileChange(string Path, string Status)
{
    /// <summary>The single letter the file pane's badge carries — the same letter git prints, so the
    /// tree and this list cannot disagree about a file (KON-427).</summary>
    public string Badge => Status.Length > 0 ? Status[..1] : string.Empty;

    public bool IsAdded => Status is "Added" or "Untracked";
    public bool IsModified => Status is "Modified" or "Renamed" or "Changed";
    public bool IsRemoved => Status is "Deleted";
}

/// <summary>
/// The state of a workspace's repository. <c>Ahead</c>/<c>Behind</c> come from the porcelain branch
/// header — being behind the remote is a warning, never a block (Plan §7): the workspace's job is to
/// let you keep writing, not to force a pull first.
/// </summary>
public sealed record GitStatus(string Branch, int Ahead, int Behind, IReadOnlyList<GitFileChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Behind the remote is a warning the page shows, never a command it refuses (Plan §7).</summary>
    public bool IsBehind => Behind > 0;

    /// <summary>"2 ahead · 1 behind", or "up to date" — one line rather than two counters that read as
    /// zero when they are simply absent.</summary>
    public string SyncLabel => (Ahead, Behind) switch
    {
        (0, 0) => "up to date",
        (_, 0) => $"{Ahead} ahead",
        (0, _) => $"{Behind} behind",
        _ => $"{Ahead} ahead · {Behind} behind",
    };

    public string ChangeLabel => Changes.Count == 1 ? "1 file changed" : $"{Changes.Count} files changed";
}
