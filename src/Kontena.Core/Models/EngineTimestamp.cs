namespace Kontena.Core.Models;

/// <summary>
/// Turns a timestamp from an engine into a <see cref="DateTimeOffset"/> without crashing on the ones
/// that are not really timestamps (KON-160).
/// </summary>
/// <remarks>
/// <para>
/// The implicit <see cref="DateTime"/> to <see cref="DateTimeOffset"/> conversion applies the local
/// offset when the value's <see cref="DateTimeKind"/> is <c>Unspecified</c> or <c>Local</c>. For a
/// zero timestamp — <c>0001-01-01</c>, which is what Docker and Kubernetes send for "never" — that
/// puts the UTC instant before year 0, and .NET throws:
/// </para>
/// <para>
/// <c>The UTC time represented when the offset is applied must be between year 0 and 10,000.
/// (Parameter 'offset')</c>
/// </para>
/// <para>
/// Only east of UTC. A machine on CEST crashes where the same data on UTC or west of it is fine, so
/// this is the kind of bug that reaches a user before it reaches us — it did.
/// </para>
/// <para>
/// Valid values are converted exactly as before, deliberately: this closes a crash and must not move
/// any time that is currently displayed correctly. Whether an <c>Unspecified</c> value should be read
/// as UTC rather than as local time is a real and separate question — engines report UTC — and is
/// noted on the ticket rather than fixed here.
/// </para>
/// </remarks>
public static class EngineTimestamp
{
    /// <summary>
    /// The value as an offset, or <c>default</c> when it is not a usable instant. "Not set" is a thing
    /// engines say all the time; it is not an error, and it is certainly not a reason to fail a page.
    /// </summary>
    public static DateTimeOffset From(DateTime value) =>
        IsUsable(value) ? new DateTimeOffset(value) : default;

    /// <summary>
    /// The optional form: null stays null, and so does a zero timestamp — for a nullable field, "never
    /// started" and "started at the beginning of time" are not the same claim.
    /// </summary>
    public static DateTimeOffset? FromOptional(DateTime? value) =>
        value is { } present && IsUsable(present) ? new DateTimeOffset(present) : null;

    /// <summary>
    /// Whether applying any offset to this leaves it representable. Both ends: <c>DateTime.MaxValue</c>
    /// west of UTC overflows the same way <c>MinValue</c> underflows east of it, and guarding one edge
    /// while knowing about the other is how the second report gets written.
    /// </summary>
    private static bool IsUsable(DateTime value) => value.Year is > 1 and < 9999;
}
