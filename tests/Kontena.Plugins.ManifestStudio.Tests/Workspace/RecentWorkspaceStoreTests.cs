using Kontena.Plugins.ManifestStudio.Workspace;

namespace Kontena.Plugins.ManifestStudio.Tests.Workspace;

/// <summary>
/// What Manifest Studio remembers between launches (KON-434).
/// <para>
/// Every test here hands <see cref="RecentWorkspaceStore"/> a path inside a temporary directory. The
/// parameterless constructor writes into the running user's own profile, and a suite that used it would
/// read and rewrite the developer's real list — the failure KON-433 was opened for.
/// </para>
/// </summary>
public sealed class RecentWorkspaceStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("manifest-studio-recent-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RecentWorkspaceStore Store() => new(Path.Combine(_root, "state", "manifest-studio.json"));

    /// <summary>A folder that exists and can be opened, so the store is fed the real thing.</summary>
    private ManifestWorkspace Folder(string name, bool kustomize = false)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        if (kustomize)
            File.WriteAllText(Path.Combine(path, "kustomization.yaml"), "resources: []\n");

        return ManifestWorkspace.Open(path);
    }

    [Fact]
    public void Nothing_is_remembered_before_anything_is_opened() =>
        Assert.Empty(Store().Read());

    [Fact]
    public void The_most_recently_opened_folder_comes_first()
    {
        var store = Store();
        store.Add(Folder("first"));
        store.Add(Folder("second"));

        Assert.Equal(["second", "first"], store.Read().Select(entry => entry.Name));
    }

    /// <summary>
    /// Reopening a folder moves it up rather than listing it twice — the list is meant to answer "where
    /// was I", and the folder you work in every day would otherwise fill it on its own.
    /// </summary>
    [Fact]
    public void Reopening_a_folder_moves_it_to_the_front_instead_of_repeating_it()
    {
        var store = Store();
        var first = Folder("first");
        store.Add(first);
        store.Add(Folder("second"));
        store.Add(first);

        Assert.Equal(["first", "second"], store.Read().Select(entry => entry.Name));
    }

    [Fact]
    public void Only_the_last_eight_are_kept()
    {
        var store = Store();
        for (var i = 0; i < 12; i++)
            store.Add(Folder($"folder-{i:00}"));

        var kept = store.Read();

        Assert.Equal(8, kept.Count);
        Assert.Equal("folder-11", kept[0].Name);
        Assert.Equal("folder-04", kept[^1].Name);
    }

    /// <summary>Read from the files, not guessed from the name — and read once, when it was opened.</summary>
    [Fact]
    public void A_kustomize_project_is_remembered_as_one()
    {
        var store = Store();
        store.Add(Folder("overlays", kustomize: true));
        store.Add(Folder("plain"));

        Assert.Equal([false, true], store.Read().Select(entry => entry.IsKustomizeProject));
    }

    /// <summary>
    /// A folder that is not there is not offered — clicking it could only fail. It stays in the file
    /// though: an unmounted volume is absent today and back tomorrow, and that is precisely the case
    /// where remembering it is worth something.
    /// </summary>
    [Fact]
    public void A_folder_that_is_gone_is_not_offered_but_is_not_forgotten()
    {
        var store = Store();
        var gone = Folder("gone");
        store.Add(gone);
        store.Add(Folder("still-here"));

        Directory.Delete(gone.RootPath, recursive: true);
        Assert.Equal(["still-here"], store.Read().Select(entry => entry.Name));

        Directory.CreateDirectory(gone.RootPath);
        Assert.Equal(["still-here", "gone"], store.Read().Select(entry => entry.Name));
    }

    /// <summary>A convenience that cannot be read is one you do without; it must never stop the page.</summary>
    [Fact]
    public void An_unreadable_file_reads_as_nothing_remembered()
    {
        var path = Path.Combine(_root, "state", "manifest-studio.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not the file we wrote");

        var store = new RecentWorkspaceStore(path);

        Assert.Empty(store.Read());

        // And it recovers: the next folder opened writes a file that parses again.
        store.Add(Folder("after"));
        Assert.Equal(["after"], store.Read().Select(entry => entry.Name));
    }
}
