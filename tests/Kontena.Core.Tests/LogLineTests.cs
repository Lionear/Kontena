using Kontena.Sdk.Models;

namespace Kontena.Core.Tests;

/// <summary>
/// Reading the timestamp an engine prefixes onto a log line (KON-203).
/// <para>
/// The bug this replaces: Docker was asked for no timestamps and every line was stamped with the
/// moment it was read, so a backlog of forty lines from four different days all showed the same
/// millisecond. The Kubernetes adapter parsed them properly and was the only place that did — hence
/// one rule here, used by both.
/// </para>
/// </summary>
public sealed class LogLineTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 7, 29, 21, 23, 19, TimeSpan.Zero);

    [Fact]
    public void A_stamped_line_reports_its_own_time_and_drops_the_prefix()
    {
        // What both engines send: RFC3339 with nanoseconds, then a space, then the line.
        var entry = LogLine.Parse(
            "2026-07-19T07:57:32.123456789Z [http] accessible via http://localhost:8025/",
            LogSource.Stdout, ReadAt);

        Assert.Equal(new DateTimeOffset(2026, 7, 19, 7, 57, 32, TimeSpan.Zero), entry.Timestamp.Trim());
        Assert.Equal("[http] accessible via http://localhost:8025/", entry.Message);
    }

    [Fact]
    public void Nanoseconds_are_kept_as_far_as_they_fit()
    {
        // .NET holds seven fractional digits and both engines send nine. Losing the last two beats
        // losing the timestamp.
        var entry = LogLine.Parse("2026-07-19T07:57:32.123456789Z hello", LogSource.Stdout, ReadAt);

        Assert.Equal(1234567, entry.Timestamp.UtcDateTime.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void An_offset_other_than_utc_survives()
    {
        var entry = LogLine.Parse("2026-07-19T09:57:32.000+02:00 hello", LogSource.Stdout, ReadAt);

        Assert.Equal(new DateTimeOffset(2026, 7, 19, 7, 57, 32, TimeSpan.Zero), entry.Timestamp.ToUniversalTime());
    }

    [Theory]
    [InlineData("[db] got terminated signal, shutting down")]
    [InlineData("docker: something happened")]
    [InlineData("2026-07-19 no T, not a stamp")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_line_without_a_stamp_falls_back_to_the_moment_it_was_read(string line)
    {
        var entry = LogLine.Parse(line, LogSource.Stdout, ReadAt);

        Assert.Equal(ReadAt, entry.Timestamp);

        // And keeps every character: a line whose first word merely looked like a date must not lose
        // it to the parse attempt.
        Assert.Equal(line, entry.Message);
    }

    [Fact]
    public void A_message_that_contains_its_own_time_keeps_all_of_it()
    {
        // mailpit writes time="…" into the body. Only the engine's prefix comes off — the first space
        // and no other.
        var entry = LogLine.Parse(
            "2026-07-19T07:57:32.1Z time=\"2026/07/19 07:57:32\" level=info msg=\"[http] starting\"",
            LogSource.Stdout, ReadAt);

        Assert.Equal("time=\"2026/07/19 07:57:32\" level=info msg=\"[http] starting\"", entry.Message);
    }

    [Fact]
    public void The_source_is_carried_through_untouched()
    {
        // stderr with no level is what makes a line render as an error, so it cannot be lost here.
        var entry = LogLine.Parse("2026-07-19T07:57:32Z boom", LogSource.Stderr, ReadAt);

        Assert.Equal(LogSource.Stderr, entry.Source);
    }
}

internal static class TimestampExtensions
{
    /// <summary>The stamp to whole seconds, so an assertion can name the second without the fraction.</summary>
    public static DateTimeOffset Trim(this DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
}
