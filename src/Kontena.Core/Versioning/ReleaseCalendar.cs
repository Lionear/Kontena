using System.Globalization;

namespace Kontena.Core.Versioning;

/// <summary>
/// One published release line of a product — Docker Engine 28, Kubernetes 1.33, containerd 2.1 — and
/// whether its publisher still maintains it.
/// </summary>
/// <param name="Name">The cycle as its publisher names it, e.g. <c>28</c> or <c>1.33</c>.</param>
/// <param name="IsMaintained">Whether the publisher still supports this line.</param>
/// <param name="EolFrom">The date support ends or ended, when one is published.</param>
/// <param name="Latest">The newest release within this line, e.g. <c>28.5.2</c>.</param>
public sealed record ReleaseCycle(string Name, bool IsMaintained, DateOnly? EolFrom, string? Latest);

/// <summary>
/// Where the release cycles of a product come from. An interface because the network is the one part
/// of this that a test cannot have, not because a second implementation is planned.
/// </summary>
public interface IReleaseCalendar
{
    /// <summary>
    /// Every published cycle for one product, or null when there is no answer — offline, an unknown
    /// product, or a document that could not be read. Null is not an error; it means "say nothing".
    /// </summary>
    ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(string product, CancellationToken ct = default);
}

/// <summary>
/// What Kontena can say about the version a backend reports, measured against its publisher's own
/// calendar (KON-370).
/// </summary>
/// <param name="Cycle">The release line this version belongs to.</param>
/// <param name="IsMaintained">Whether that line is still supported by its publisher.</param>
/// <param name="EolFrom">When support ends or ended, if published.</param>
/// <param name="NewerPatch">
/// A newer release within the same line, or null when this is already the newest. Separate from
/// support: being a few patches behind on a maintained line is worth mentioning, not worth a warning.
/// </param>
public sealed record VersionSupport(string Cycle, bool IsMaintained, DateOnly? EolFrom, string? NewerPatch)
{
    /// <summary>
    /// Whether this is worth putting in front of the user — the same question
    /// <c>NodeVersionSkew.IsProblem</c> answers for the cluster side, so the two read alike.
    /// </summary>
    public bool IsProblem => !IsMaintained;

    /// <summary>
    /// The sentence behind the verdict: why the line is a problem, or — for a maintained line that is
    /// behind on patches — the newer release that exists. Empty when there is nothing to say, which is
    /// most of the time and is the point.
    /// <para>
    /// Here rather than on the switcher row that first needed it (KON-371): three screens ask this
    /// question now, and three copies of the wording is three chances for them to disagree. Named
    /// <c>Detail</c> for the same reason <c>NodeVersionSkew.Detail</c> is — both are the sentence
    /// behind a warning icon, and the tooltip should not care which of the two it is showing.
    /// </para>
    /// </summary>
    public string Detail => this switch
    {
        { IsMaintained: false, EolFrom: { } eol } =>
            $"Release {Cycle} has not been supported since {eol.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.",
        { IsMaintained: false } => $"Release {Cycle} is no longer supported.",
        { NewerPatch: { } newer } => $"{newer} is available.",
        _ => string.Empty,
    };
}
