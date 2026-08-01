using System.Globalization;
using System.Text.Json;
using Kontena.Sdk.Tooling;

namespace Kontena.Core.Tooling;

/// <summary>
/// Whether a newer release exists than the one installed, for tools Kontena knows how to fetch
/// (KON-153).
/// <para>
/// Cached, because this is the only thing on the tooling page that needs the network. An answer is
/// kept for a day: tools release every few weeks, so asking more often than that is traffic in
/// exchange for nothing. Offline the cached answer simply stands, and with no cached answer the page
/// says nothing at all — an unknown is not a warning.
/// </para>
/// </summary>
public sealed class ToolUpdateCheck(IToolReleaseSource source, ManagedToolStore? store = null)
{
    /// <summary>How long an answer stays good. A day, for tools that release every few weeks.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ManagedToolStore _store = store ?? new ManagedToolStore();

    /// <summary>
    /// The newest release next to <paramref name="installed"/>, or null when there is nothing to say:
    /// a tool Kontena cannot fetch, a lookup that failed, or a version string neither side can read.
    /// </summary>
    /// <param name="now">
    /// Passed in rather than read here so the cache can be tested without waiting a day.
    /// </param>
    public async ValueTask<ToolUpdate?> CheckAsync(
        ExternalTool tool,
        string? installed,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.Release is null)
            return null;

        var latest = Cached(tool, now) ?? await FetchAsync(tool, now, ct);

        return latest is null ? null : new ToolUpdate(latest, IsNewer(installed, latest));
    }

    private async ValueTask<string?> FetchAsync(ExternalTool tool, DateTimeOffset now, CancellationToken ct)
    {
        string? version;
        try
        {
            version = (await source.LatestAsync(tool, ct))?.Version;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Being offline is not news. The page shows what it knew before, or nothing.
            return null;
        }

        if (version is not null)
            Store(tool, version, now);

        return version;
    }

    /// <summary>
    /// Whether the newest release is genuinely ahead. Reuses the version comparison the minimum-version
    /// check uses, so the two never disagree about which of two strings is older.
    /// </summary>
    private static bool IsNewer(string? installed, string latest) =>
        !string.IsNullOrWhiteSpace(installed) && ToolReadinessCheck.IsOlder(installed, latest);

    private string CachePath(ExternalTool tool) => _store.PathFor(tool) + ".latest";

    private string? Cached(ExternalTool tool, DateTimeOffset now)
    {
        var path = CachePath(tool);
        if (!File.Exists(path))
            return null;

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2
            || !DateTimeOffset.TryParse(
                lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
        {
            return null;
        }

        // A clock that went backwards would otherwise pin a stale answer in place forever.
        var age = now - checkedAt;
        return age >= TimeSpan.Zero && age < MaxAge && lines[0].Length > 0 ? lines[0] : null;
    }

    private void Store(ExternalTool tool, string version, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(_store.Root);
            File.WriteAllLines(
                CachePath(tool),
                [version, now.ToString("O", CultureInfo.InvariantCulture)]);
        }
        catch (IOException)
        {
            // A cache that cannot be written costs one extra lookup next time, which is not worth
            // failing a page over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
