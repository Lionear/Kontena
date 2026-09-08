using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Sdk;

namespace Kontena.Plugins.ManifestStudio.Workspace;

/// <summary>
/// A folder that has been opened as a workspace before (KON-434).
/// <para>
/// Only what was known the moment it was opened. Whether it is a Kustomize project costs a recursive
/// walk of the whole tree (<see cref="ManifestWorkspace.Open"/>), and the branch and change count the
/// mockup also shows cost a <c>git</c> process each — none of which a list on an empty page should pay
/// for folders the user may not even click.
/// </para>
/// </summary>
/// <param name="RootPath">The folder itself, absolute — the only thing that identifies it.</param>
/// <param name="IsKustomizeProject">What it was the last time it was opened.</param>
public sealed record RecentWorkspace(string RootPath, bool IsKustomizeProject)
{
    /// <summary>The folder's own name, which is what the row is read by; the path says which one it is.</summary>
    [JsonIgnore]
    public string Name =>
        Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>
/// The folders Manifest Studio offers to reopen, most-recent first.
/// <para>
/// Its own small file beside the application data, the way <c>RolloutRecordStore</c> keeps an
/// interrupted rollout. A plugin may reference nothing but <c>Kontena.Sdk</c> (CONTRIBUTING.md §4,
/// enforced by <c>ExtensionBoundaryTests</c>), so the host's <c>KontenaSettings</c> — where
/// <c>RecentBuildContexts</c> keeps exactly this kind of list for the Build modal — cannot be reached
/// from here. If a second plugin ever wants the same thing, the answer is a store the host hands out
/// through <c>IPluginHost</c>, not a second copy of this file.
/// </para>
/// </summary>
public sealed class RecentWorkspaceStore
{
    /// <summary>
    /// How many to keep: enough for the folders someone actually alternates between, few enough that
    /// the empty state stays a page about opening a workspace.
    /// </summary>
    private const int Keep = 8;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    /// <param name="path">
    /// Where to write. Defaults next to the other application data; tests pass a temporary file,
    /// because the default is the running user's real profile (KON-433).
    /// </param>
    public RecentWorkspaceStore(string? path = null) =>
        _path = path ?? Path.Combine(ProductInfo.DataDirectory, "manifest-studio.json");

    /// <summary>
    /// The folders worth offering, most-recent first. One that is not there right now is left out but
    /// not forgotten — a folder on an unmounted volume is absent today and back tomorrow, and dropping
    /// it on sight would lose it in the one case where remembering it helps most.
    /// </summary>
    public IReadOnlyList<RecentWorkspace> Read() =>
        [.. ReadStored().Where(entry => Directory.Exists(entry.RootPath))];

    /// <summary>Puts this workspace at the front, keeping the file to <see cref="Keep"/> entries.</summary>
    public void Add(ManifestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // Stored rather than Read: an entry left out because its volume is not mounted right now must
        // not be dropped by the next folder somebody happens to open.
        var kept = ReadStored()
            .Where(entry => !string.Equals(entry.RootPath, workspace.RootPath, StringComparison.Ordinal))
            .Take(Keep - 1);

        Write([new RecentWorkspace(workspace.RootPath, workspace.IsKustomizeProject), .. kept]);
    }

    /// <summary>What is in the file, unfiltered. A convenience that cannot be read is one you do
    /// without — never a reason for the page not to open.</summary>
    private List<RecentWorkspace> ReadStored()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<RecentWorkspace>>(File.ReadAllText(_path), Json) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(IReadOnlyList<RecentWorkspace> entries)
    {
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the shortcut is not worth failing the folder the user just opened.
        }
    }
}
