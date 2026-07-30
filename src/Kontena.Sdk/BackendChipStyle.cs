namespace Kontena.Sdk;

/// <summary>
/// How a backend's chip is drawn (KON-80). A provider declares its own mark and colour, so a
/// store-installed adapter ships its logo with itself instead of needing a case in the app — the same
/// reasoning as <see cref="IBackendProvider"/> carrying <see cref="IBackendProvider.DisplayName"/>.
/// </summary>
/// <param name="Glyph">
/// Filled path data for the mark, in a 24x24 box. Path data rather than an image because a chip is one
/// silhouette in one colour at four different sizes, and because a string crosses the plugin boundary
/// without the adapter needing a UI framework reference.
/// </param>
/// <param name="Accent">
/// The brand colour as <c>#RRGGBB</c>, used for the mark and, tinted, for the plate behind it. One colour
/// rather than two: a chip whose glyph and plate can disagree is how Podman ended up wearing Docker blue.
/// </param>
public sealed record BackendChipStyle(string Glyph, string Accent);
