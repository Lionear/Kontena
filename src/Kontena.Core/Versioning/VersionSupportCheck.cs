using System.Globalization;
using System.Text.Json;
using Kontena.Sdk.Tooling;

namespace Kontena.Core.Versioning;

/// <summary>
/// Whether the version a backend reports still sits on a release line its publisher maintains
/// (KON-370).
/// <para>
/// Cached for a day, in the shape <see cref="Kontena.Core.Tooling.ToolUpdateCheck"/> already
/// established: support windows move a few times a year, so asking more often than daily is traffic in
/// exchange for nothing. Offline the cached answer stands — an end-of-life date does not move backwards
/// — and with no cached answer nothing is shown at all. An unknown is not a warning.
/// </para>
/// <para>
/// The whole product document is fetched and compared here, rather than asking a service about one
/// version. That is what keeps this from telling anyone which versions are running on this machine.
/// </para>
/// </summary>
public sealed class VersionSupportCheck(IReleaseCalendar calendar, string? cacheRoot = null)
{
    /// <summary>How long an answer stays good.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly string _root = cacheRoot ?? DefaultRoot();

    /// <summary>Beside the tool store, under the platform's application-data directory.</summary>
    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear", "Kontena", "versions");

    /// <summary>
    /// What can be said about <paramref name="installed"/>, or null when there is nothing to say: a
    /// backend with no published calendar, a version string that could not be read, a lookup that
    /// failed, or a release the calendar does not list.
    /// </summary>
    /// <param name="product">A product from <see cref="BackendProducts"/>; null means say nothing.</param>
    /// <param name="now">
    /// Passed in rather than read here so the cache can be tested without waiting a day.
    /// </param>
    public async ValueTask<VersionSupport?> CheckAsync(
        string? product,
        string? installed,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(product))
            return null;

        // Reuses the readiness check's reader, so this and the tooling page never disagree about what
        // the version in a string like "v2.1.9" or "v1.33.4-gke.1043000" is.
        if (ToolReadinessCheck.Number(installed) is not { } number)
            return null;

        var cycles = Cached(product, now) ?? await FetchAsync(product, now, ct);

        return cycles is null ? null : Match(cycles, number);
    }

    /// <summary>
    /// The cycle this version belongs to, most specific first: containerd publishes both a <c>2</c> and
    /// a <c>2.1</c> line, and matching the shorter one would measure a 2.1 install against someone
    /// else's support dates.
    /// </summary>
    private static VersionSupport? Match(IReadOnlyList<ReleaseCycle> cycles, string number)
    {
        var installed = Numbers(number);
        ReleaseCycle? best = null;
        var bestLength = 0;

        foreach (var cycle in cycles)
        {
            var name = Numbers(cycle.Name);

            // A cycle named something other than a plain number (a vendor's "2.0-lts") is one we cannot
            // place a version into, so it never wins.
            if (name.Length == 0 || name.Length > installed.Length || name.Length <= bestLength)
                continue;

            if (name.SequenceEqual(installed.Take(name.Length)))
                (best, bestLength) = (cycle, name.Length);
        }

        if (best is null)
            return null;

        var newer = best.Latest is { } latest && ToolReadinessCheck.IsOlder(number, latest) ? latest : null;

        return new VersionSupport(best.Name, best.IsMaintained, best.EolFrom, newer);
    }

    private static int[] Numbers(string text) =>
        [.. text.Split('.').Select(part => int.TryParse(part, CultureInfo.InvariantCulture, out var n) ? n : -1)];

    private async ValueTask<IReadOnlyList<ReleaseCycle>?> FetchAsync(
        string product, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<ReleaseCycle>? cycles;
        try
        {
            cycles = await calendar.CyclesAsync(product, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Being offline is not news. The page shows what it knew before, or nothing.
            return null;
        }

        if (cycles is not null)
            Store(product, cycles, now);

        return cycles;
    }

    /// <summary>
    /// Null for a product name that would not make a plain file name. The map that feeds this only
    /// holds constants, but this builds a path out of a parameter, and a path built out of a parameter
    /// gets checked.
    /// </summary>
    private string? CachePath(string product) =>
        product.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.')
            ? null
            : Path.Combine(_root, $"{product}.json");

    private ReleaseCycle[]? Cached(string product, DateTimeOffset now)
    {
        if (CachePath(product) is not { } path || !File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            var split = text.IndexOf('\n', StringComparison.Ordinal);
            if (split < 0)
                return null;

            if (!DateTimeOffset.TryParse(
                    text[..split], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
            {
                return null;
            }

            // A clock that went backwards would otherwise pin a stale answer in place forever.
            var age = now - checkedAt;
            if (age < TimeSpan.Zero || age >= MaxAge)
                return null;

            return JsonSerializer.Deserialize<ReleaseCycle[]>(text[(split + 1)..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Store(string product, IReadOnlyList<ReleaseCycle> cycles, DateTimeOffset now)
    {
        if (CachePath(product) is not { } path)
            return;

        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(
                path,
                $"{now.ToString("O", CultureInfo.InvariantCulture)}\n{JsonSerializer.Serialize(cycles)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written costs one extra lookup next time, which is not worth
            // failing a page over.
        }
    }
}
