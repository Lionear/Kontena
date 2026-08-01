using System.Globalization;

namespace Kontena.Sdk.Models;

/// <summary>
/// Reads the timestamp an engine prefixes onto a log line (KON-203).
/// <para>
/// Both Docker and the kubelet, asked for timestamps, put an RFC3339 stamp and a space in front of
/// every line. Only the Kubernetes adapter did that; Docker asked for no timestamps and then stamped
/// each line with <c>DateTimeOffset.UtcNow</c> — the moment it was <i>read</i>. Fetching history
/// arrives in one go, so forty lines from four different days all carried the same millisecond, which
/// looks like information and is not.
/// </para>
/// <para>
/// One rule, two adapters: the second implementation had already drifted from the first, which is what
/// this replaces.
/// </para>
/// </summary>
public static class LogLine
{
    /// <summary>
    /// The entry a raw line describes. <paramref name="readAt"/> is used only when the line carries no
    /// parseable stamp of its own — a container that writes a bare line, or an engine that was asked
    /// for timestamps and did not send them.
    /// </summary>
    public static LogEntry Parse(string line, LogSource source, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(line);

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && TryParseStamp(line[..space], out var stamp))
            return new LogEntry(stamp, source, line[(space + 1)..]);

        return new LogEntry(readAt, source, line);
    }

    /// <summary>
    /// An RFC3339 stamp, or not. Nanoseconds are truncated to what <see cref="DateTimeOffset"/> can
    /// hold: both engines send nine fractional digits and .NET parses seven, and a line is not worth
    /// losing its time over the two it cannot keep.
    /// </summary>
    private static bool TryParseStamp(string candidate, out DateTimeOffset stamp)
    {
        stamp = default;

        // Cheap reject before the parse: a log line that happens to start with a word must not cost a
        // full date parse per line, and "docker" or "[db]" is what most of them start with.
        if (candidate.Length < 20 || !char.IsAsciiDigit(candidate[0]))
            return false;

        return DateTimeOffset.TryParse(
            Truncate(candidate), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out stamp);
    }

    private static string Truncate(string candidate)
    {
        var dot = candidate.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
            return candidate;

        var end = dot + 1;
        while (end < candidate.Length && char.IsAsciiDigit(candidate[end]))
            end++;

        const int maxFraction = 7;
        return end - dot - 1 <= maxFraction
            ? candidate
            : candidate[..(dot + 1 + maxFraction)] + candidate[end..];
    }
}
