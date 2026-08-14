namespace Kontena.Adapters.Apple;

/// <summary>
/// The Apple mark, as path data for the backend chip (KON-80).
/// <para>
/// It lived in <c>Kontena.App</c> while the onboarding row was a promise the UI made rather than a
/// provider being probed; with the adapter here (KON-31) it moved beside <c>DockerBrand</c> and
/// friends, which is where its own file said it belonged.
/// </para>
/// <para>
/// Source: <c>apple.svg</c> from <see href="https://github.com/simple-icons/simple-icons">simple-icons</see>
/// (CC0-1.0). The mark is a trademark of Apple Inc.; used nominatively to name the runtime, and Kontena
/// is not affiliated with or endorsed by Apple Inc.
/// </para>
/// </summary>
public static class AppleBrand
{
    /// <summary>Graphite rather than black: the mark has to survive both themes, and black cannot.</summary>
    public const string Accent = "#8E8E93";

    /// <summary>Filled path data, 24x24.</summary>
    public const string Glyph =
        "M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701";
}
