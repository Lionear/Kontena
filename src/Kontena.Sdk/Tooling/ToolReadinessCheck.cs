using System.Text.RegularExpressions;

namespace Kontena.Sdk.Tooling;

/// <summary>
/// Works out what state a tool is in: system install, Kontena's own copy, or nothing at all.
/// </summary>
public sealed partial class ToolReadinessCheck(IToolRunner runner, ManagedToolStore? store = null)
{
    private readonly ManagedToolStore _store = store ?? new ManagedToolStore();

    /// <summary>
    /// A system install wins over a managed copy, unless this tool was explicitly handed to Kontena
    /// (KON-153). Someone who installed kind themselves expects Kontena to use that one — and if both
    /// exist and nobody said otherwise, ours is the stale one nobody is watching.
    /// <para>
    /// Deliberately the same precedence <see cref="ManagedTools.ResolveAsync"/> applies, so what the
    /// settings page reports is what actually runs.
    /// </para>
    /// </summary>
    public async ValueTask<ToolReadiness> CheckAsync(ExternalTool tool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var preferred = _store.IsPreferred(tool);

        if (!preferred)
        {
            var system = await runner.FindAsync(tool, ct);
            if (system.Found)
                return Describe(tool, system.Path, system.Version, managed: false);
        }

        if (await _store.VerifiedPathAsync(tool, ct) is { } managed)
        {
            var record = _store.Record(tool);
            return Describe(tool, managed, record?.Version, managed: true) with { Preferred = preferred };
        }

        // Preferred, but the copy is gone or no longer hashes to what was recorded. Fall back to the
        // system install rather than reporting nothing: the preference is about which one wins when
        // there is a choice, not a promise to refuse the other one.
        if (preferred && await runner.FindAsync(tool, ct) is { Found: true } fallback)
            return Describe(tool, fallback.Path, fallback.Version, managed: false);

        return new ToolReadiness(tool, ToolState.Missing, null, null, false, PackageManagers.Best(tool));
    }

    /// <summary>Check several at once — the settings page shows them as a group.</summary>
    public async ValueTask<IReadOnlyList<ToolReadiness>> CheckAllAsync(
        IEnumerable<ExternalTool> tools, CancellationToken ct = default)
    {
        var results = new List<ToolReadiness>();
        foreach (var tool in tools)
            results.Add(await CheckAsync(tool, ct));

        return results;
    }

    private static ToolReadiness Describe(ExternalTool tool, string? path, string? version, bool managed)
    {
        if (version is null)
            return new ToolReadiness(tool, ToolState.Unusable, path, null, managed, PackageManagers.Best(tool));

        var outdated = tool.MinimumVersion is { } minimum && IsOlder(version, minimum);

        return new ToolReadiness(
            tool,
            outdated ? ToolState.Outdated : ToolState.Ready,
            path,
            version,
            managed,
            outdated ? PackageManagers.Best(tool) : null);
    }

    /// <summary>
    /// Compare two versions on their numeric parts alone.
    /// <para>
    /// Tools answer with whatever they feel like — <c>kind v0.31.0 go1.25.5 linux/amd64</c>,
    /// <c>v4.2.2+gb05881c</c>, <c>Client Version: v1.34.9</c> — so the first dotted number in the
    /// string is the version and the rest is noise. A version that cannot be read is treated as new
    /// enough: refusing to work over an unparsable string would be Kontena's problem presented as
    /// the user's.
    /// </para>
    /// </summary>
    public static bool IsOlder(string version, string minimum)
    {
        var left = Numbers(version);
        var right = Numbers(minimum);

        if (left.Length == 0 || right.Length == 0)
            return false;

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;

            if (a != b)
                return a < b;
        }

        return false;
    }

    /// <summary>
    /// Just the version number out of whatever the tool answered — <c>kind v0.31.0 go1.25.5
    /// linux/amd64</c> becomes <c>0.31.0</c>. Null when there is no number in there at all.
    /// <para>
    /// For showing, not for comparing: the full line is the honest answer to "what is installed", but
    /// it is too long to sit in a summary next to three other tools.
    /// </para>
    /// </summary>
    public static string? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = VersionPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    private static int[] Numbers(string text)
    {
        var match = VersionPattern().Match(text);
        return match.Success
            ? [.. match.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0)]
            : [];
    }

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionPattern();
}
