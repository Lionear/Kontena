using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kontena.Plugins.ManifestStudio.Git;

/// <summary>
/// Cloning a repository straight into a workspace (KON-436) — the other half of KON-434's start page:
/// open a folder you already have, or fetch one you do not.
/// <para>
/// You give it a URL and the folder to clone <em>into</em>, the way <c>git clone</c> itself is used:
/// the repository's own name becomes the new folder, so picking a parent twice for two repositories
/// does not silently nest one inside the other. Errors land in <see cref="Error"/> rather than throwing
/// out of the command, same reasoning as <see cref="GitViewModel"/>.
/// </para>
/// </summary>
public sealed partial class CloneViewModel(GitCli git) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private string _url = string.Empty;

    /// <summary>The folder to clone into — chosen with the same picker the "Open folder…" button uses.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private string? _parentFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// The last thing git said while it works — "Receiving objects: 47% (470/1000)" and the rest.
    /// A clone has no length this side knows until git counts it, so its own words are the only honest
    /// progress there is; the same reasoning that keeps the cluster provisioners streaming (KON-231).
    /// </summary>
    [ObservableProperty]
    private string _progress = string.Empty;

    /// <summary>Where the clone will land, or null while there is not enough to say.</summary>
    public string? TargetPath => ParentFolder is { Length: > 0 } parent && FolderNameFor(Url) is { } name
        ? Path.Combine(parent, name)
        : null;

    /// <summary>Raised with the folder the clone landed in, for whoever opens it as the workspace.</summary>
    public event EventHandler<string>? Cloned;

    /// <summary>
    /// The folder a repository URL would clone into, which is what git itself would pick: the last
    /// segment without its <c>.git</c> suffix. Handles <c>git@host:org/repo.git</c> as well as an
    /// <c>https://</c> URL, because the colon form has no slash before the path.
    /// </summary>
    private static string? FolderNameFor(string url)
    {
        var trimmed = url.Trim().TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var name = separator < 0 ? trimmed : trimmed[(separator + 1)..];

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : null;
    }

    private bool CanClone() => !IsBusy && TargetPath is not null;

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task Clone()
    {
        var target = TargetPath!;

        IsBusy = true;
        Error = null;
        Progress = string.Empty;

        try
        {
            // Nothing is checked about the target first: a folder that exists, is not empty, or cannot
            // be written is git's answer to give, and "destination path already exists and is not an
            // empty directory" is a better sentence than one guessed at from a File.Exists here.
            var result = await git.CloneAsync(Url.Trim(), target, line => Progress = line);

            Error = result.Error;
            if (result.Ok)
                Cloned?.Invoke(this, target);
        }
        finally
        {
            IsBusy = false;
            Progress = string.Empty;
        }
    }
}
