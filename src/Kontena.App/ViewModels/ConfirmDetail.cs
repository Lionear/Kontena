namespace Kontena.App.ViewModels;

/// <summary>
/// One line in a confirmation's list of what goes away (KON-162).
/// </summary>
/// <remarks>
/// Prose is the wrong shape for an inventory. "Its 4 containers are stopped and removed, along with
/// the networks Compose created for it" makes the reader parse a sentence to find out what they are
/// about to lose; a list lets them count it. The sentence stays for what <i>survives</i>, which is a
/// claim rather than an inventory.
/// <para>
/// In a destructive confirmation, only ever what actually goes. A line here is a promise that the
/// thing is deleted, and a dialog that over-promises is worse than one that says nothing — the next
/// time it is believed less.
/// <para>
/// A confirmation that destroys nothing uses the same list for what the answer is <i>about</i>: what
/// a metrics-server install creates (KON-93), or the fingerprints of a host being trusted (KON-260).
/// The rule is the same in both — every line is a claim, and it has to be true.
/// </para>
/// </para>
/// </remarks>
/// <param name="Icon">Resource key of the glyph, e.g. <c>IconBox</c>. Resolved in the view.</param>
/// <param name="Headline">The count and its noun, e.g. "4 containers".</param>
/// <param name="Detail">Which ones, e.g. "web, db, redis, worker". Empty when naming them adds nothing.</param>
public sealed record ConfirmDetail(string Icon, string Headline, string Detail = "")
{
    public bool HasDetail => Detail.Length > 0;
}
