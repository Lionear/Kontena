using System.Globalization;
using System.Text.RegularExpressions;

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
public static partial class LogLine
{
    /// <summary>
    /// The entry a raw line describes. <paramref name="readAt"/> is used only when the line carries no
    /// parseable stamp of its own — a container that writes a bare line, or an engine that was asked
    /// for timestamps and did not send them.
    /// </summary>
    public static LogEntry Parse(string line, LogSource source, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(line);

        line = StripAnsi(line);

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && TryParseStamp(line[..space], out var stamp))
            return new LogEntry(stamp, source, line[(space + 1)..]);

        return new LogEntry(readAt, source, line);
    }

    /// <summary>
    /// The line without its colour (KON-469).
    /// <para>
    /// A container writes to what it believes is a terminal, so plenty of them colour their output —
    /// the systemd boot log inside a kindest/node is the one that gets noticed, painting every
    /// <c>[ OK ]</c> green. The console shows text, not a terminal, so those bytes were drawn
    /// literally: <c>[0;32m OK [0m Finished ...</c>. Stripping here rather than in the viewers means
    /// the row, its text filter and what Ctrl+C puts on the clipboard (KON-452/463) all read the same
    /// clean line, because all three are that one string.
    /// </para>
    /// <para>
    /// Unlike a tool this app starts itself, a running container cannot be told <c>NO_COLOR</c> after
    /// the fact, so the stripping has to happen on this side.
    /// </para>
    /// </summary>
    private static string StripAnsi(string line) =>
        line.Contains('\e', StringComparison.Ordinal) ? Csi().Replace(line, string.Empty) : line;

    /// <summary>
    /// A CSI escape sequence, by its actual grammar rather than the digits-and-semicolons shorthand:
    /// systemd alone sends <c>\e[?25l</c> and <c>\e[K</c> as well as <c>\e[0;32m</c>, and the private
    /// <c>?</c> and the bare form are not digits.
    /// </summary>
    // ponytail: CSI only. OSC (window titles) and the two-character sequences would each want their
    // own pattern; no log we have seen carries them, and a leftover lone \e draws as nothing anyway.
    [GeneratedRegex(@"\e\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Csi();

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
