namespace Kontena.Core.Tooling;

/// <summary>
/// What the publisher's newest release is, next to what is installed (KON-153).
/// <para>
/// Deliberately not a <see cref="ToolState"/>. That enum answers "can Kontena use this", and a tool
/// one release behind can be used perfectly well — folding the two together would turn a working
/// install orange for no reason anyone can act on. This is the other axis: information, not a problem.
/// </para>
/// </summary>
/// <param name="Latest">The newest release the publisher offers, as they name it.</param>
/// <param name="IsNewer">Whether that is actually ahead of what is installed.</param>
public sealed record ToolUpdate(string Latest, bool IsNewer);
