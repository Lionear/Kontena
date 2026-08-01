using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// The zero-timestamp crash (KON-160). Reported from a Windows machine on CEST; reproduced exactly,
/// down to the message, and the reason it never showed up here is that it cannot happen west of UTC.
/// </summary>
public class EngineTimestampTests
{
    /// <summary>
    /// The conversion .NET refuses. Pinned as its own test so the reason these guards exist stays
    /// visible: without them this is the exception, and it reached a user as "can't reach a container
    /// engine".
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void The_conversion_this_guards_against_really_does_throw(DateTimeKind kind)
    {
        var zero = DateTime.SpecifyKind(DateTime.MinValue, kind);

        // Only where the machine is east of UTC — which is the whole difficulty of the bug, so the
        // test says what it depends on rather than pretending to be universal.
        if (TimeZoneInfo.Local.GetUtcOffset(DateTime.UnixEpoch) <= TimeSpan.Zero)
            return;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new DateTimeOffset(zero));
        Assert.Equal("offset", ex.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public void A_zero_timestamp_is_not_set_rather_than_a_crash(DateTimeKind kind)
    {
        var zero = DateTime.SpecifyKind(DateTime.MinValue, kind);

        Assert.Equal(default, EngineTimestamp.From(zero));
        Assert.Null(EngineTimestamp.FromOptional(zero));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public void The_far_end_is_guarded_too(DateTimeKind kind)
    {
        // MaxValue overflows west of UTC exactly as MinValue underflows east of it. Guarding one edge
        // while knowing about the other is how the second bug report gets written.
        var far = DateTime.SpecifyKind(DateTime.MaxValue, kind);

        Assert.Equal(default, EngineTimestamp.From(far));
        Assert.Null(EngineTimestamp.FromOptional(far));
    }

    [Fact]
    public void Nothing_stays_nothing()
    {
        Assert.Null(EngineTimestamp.FromOptional(null));
    }

    [Fact]
    public void A_real_timestamp_converts_exactly_as_it_did_before()
    {
        // The point of the fix is a crash, not a correction: a value that works today must land on
        // precisely the same instant, or this quietly moves every time on screen.
        var value = new DateTime(2026, 7, 27, 21, 42, 8, DateTimeKind.Utc);

        Assert.Equal(new DateTimeOffset(value), EngineTimestamp.From(value));
        Assert.Equal(new DateTimeOffset(value), EngineTimestamp.FromOptional(value));
    }

    [Fact]
    public void A_local_timestamp_keeps_its_offset()
    {
        var value = new DateTime(2026, 7, 27, 21, 42, 8, DateTimeKind.Local);

        Assert.Equal(new DateTimeOffset(value), EngineTimestamp.From(value));
    }

    [Fact]
    public void The_unix_epoch_is_a_perfectly_good_timestamp()
    {
        // Docker reports 1970 for a container whose created time is zero seconds. That is a real
        // instant and must survive — the guard is about year 1, not about "suspiciously old".
        Assert.Equal(DateTimeOffset.UnixEpoch, EngineTimestamp.From(DateTime.UnixEpoch));
    }
}
