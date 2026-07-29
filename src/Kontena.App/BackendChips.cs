using Kontena.Engines;

namespace Kontena.App;

/// <summary>
/// What to draw in a backend chip: the mark and colour a provider declared, or the letter to fall back
/// to (KON-80).
/// </summary>
/// <param name="Letter">Shown when there is no mark — demo backends, and a remote engine on purpose.</param>
/// <param name="Glyph">Filled path data in a 24x24 box, or null.</param>
/// <param name="Accent">Brand colour as <c>#RRGGBB</c>, or null.</param>
public sealed record BackendChipInfo(string Letter, string? Glyph = null, string? Accent = null)
{
    /// <summary>Whether there is a mark to draw at all.</summary>
    public bool HasGlyph => Glyph is { Length: > 0 } && Accent is { Length: > 0 };

    /// <summary>The chip a provider asks for — the only source that can speak for a plugin's own logo.</summary>
    public static BackendChipInfo For(IBackendProvider provider) =>
        new(provider.Chip, provider.ChipStyle?.Glyph, provider.ChipStyle?.Accent);
}

/// <summary>
/// Resolves a chip from a backend id alone (KON-80).
/// <para>
/// Most chips are drawn next to a provider and can just ask it. A container row cannot: a
/// <c>ContainerSummary</c> carries the backend id that owns it and nothing else, and rows outlive the
/// provider list being rebuilt. So the ids the providers declared are remembered here, once, when the
/// registry is built — rather than the app keeping a second table of logos that has to agree with the
/// adapters' one.
/// </para>
/// </summary>
public static class BackendChips
{
    private static readonly Dictionary<string, BackendChipInfo> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remember what the current providers declare. Called whenever the registry is built or replaced;
    /// replaces the previous set, because a provider that is gone should not keep contributing a logo.
    /// </summary>
    public static void Learn(IEnumerable<IBackendProvider> providers)
    {
        Known.Clear();
        foreach (var provider in providers)
        {
            if (provider.ChipStyle is not { } style)
                continue;

            var info = new BackendChipInfo(provider.Chip, style.Glyph, style.Accent);
            Known[provider.Backend] = info;
            // A family key as well, because a cluster id carries its context ("kubernetes:prod-eu-west")
            // and a pod row only ever knows the whole id. Note that a remote engine's id is its own
            // family ("docker-remote:…"), so it keeps its letter — which is the point of that letter.
            Known.TryAdd(Family(provider.Backend), info);
        }
    }

    /// <summary>
    /// The chip for a backend id, falling back to a letter badge derived from the id. Never null: a
    /// backend nobody declared a logo for still has to be drawn, and "?" is what an empty id gets.
    /// </summary>
    public static BackendChipInfo For(string? backend)
    {
        if (string.IsNullOrEmpty(backend))
            return new BackendChipInfo("?");

        if (Known.TryGetValue(backend, out var exact))
            return exact;

        if (Known.TryGetValue(Family(backend), out var family))
            return family;

        return new BackendChipInfo(backend[..1].ToUpperInvariant());
    }

    /// <summary>
    /// The engine kind out of a backend id: everything before the first <c>:</c> or <c>@</c>, which is
    /// how the ids are built ("kubernetes:prod-eu-west", "kubernetes@a1b2c3:default").
    /// </summary>
    private static string Family(string backend)
    {
        var cut = backend.IndexOfAny([':', '@']);
        return cut < 0 ? backend : backend[..cut];
    }
}
