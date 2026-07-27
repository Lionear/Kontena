using Kontena.Core.Tooling;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Noticing that a newer release exists, without turning the tooling page into a thing that waits on
/// the network every time it opens (KON-153).
/// </summary>
public class ToolUpdateCheckTests
{
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Counts its calls, because "how often do we ask" is what the cache is for.</summary>
    private sealed class Source(string? version, Exception? throws = null) : IToolReleaseSource
    {
        public int Calls { get; private set; }

        public ValueTask<ToolDownload?> LatestAsync(ExternalTool tool, CancellationToken ct = default)
        {
            Calls++;

            if (throws is not null)
                throw throws;

            return ValueTask.FromResult(version is null
                ? null
                : new ToolDownload(tool, version, new Uri("https://example.invalid/x"), new string('a', 64)));
        }
    }

    [Fact]
    public async Task A_newer_release_is_reported_as_newer()
    {
        var check = new ToolUpdateCheck(new Source("v0.32.0"), EmptyStore());

        var update = await check.CheckAsync(KnownTools.Kind, "kind v0.31.0 go1.25.5 linux/amd64", Now);

        // The installed version arrives as the tool's own paragraph, not as a clean number — that is
        // what the readiness check's comparison already handles, and this reuses it rather than
        // growing a second opinion about which of two strings is older.
        Assert.Equal(new ToolUpdate("v0.32.0", IsNewer: true), update);
    }

    [Fact]
    public async Task Being_up_to_date_is_reported_too_but_not_as_news()
    {
        var check = new ToolUpdateCheck(new Source("v0.32.0"), EmptyStore());

        var update = await check.CheckAsync(KnownTools.Kind, "kind v0.32.0", Now);

        // Still an answer, so a caller can say "up to date" if it wants; the page simply does not.
        Assert.Equal(new ToolUpdate("v0.32.0", IsNewer: false), update);
    }

    [Fact]
    public async Task The_answer_is_asked_for_once_a_day()
    {
        var source = new Source("v0.32.0");
        var store = EmptyStore();

        await new ToolUpdateCheck(source, store).CheckAsync(KnownTools.Kind, "v0.31.0", Now);
        await new ToolUpdateCheck(source, store).CheckAsync(KnownTools.Kind, "v0.31.0", Now.AddHours(23));

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task After_a_day_it_asks_again()
    {
        var source = new Source("v0.32.0");
        var store = EmptyStore();

        await new ToolUpdateCheck(source, store).CheckAsync(KnownTools.Kind, "v0.31.0", Now);
        await new ToolUpdateCheck(source, store).CheckAsync(KnownTools.Kind, "v0.31.0", Now.AddHours(25));

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_clock_that_went_backwards_does_not_pin_a_stale_answer()
    {
        var source = new Source("v0.32.0");
        var store = EmptyStore();

        await new ToolUpdateCheck(source, store).CheckAsync(KnownTools.Kind, "v0.31.0", Now);
        await new ToolUpdateCheck(source, store).CheckAsync(KnownTools.Kind, "v0.31.0", Now.AddDays(-3));

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Being_offline_says_nothing_rather_than_something_wrong()
    {
        var check = new ToolUpdateCheck(new Source(null, new HttpRequestException("no route")), EmptyStore());

        // An unknown is not a warning. The row simply carries no update line.
        Assert.Null(await check.CheckAsync(KnownTools.Kind, "v0.31.0", Now));
    }

    [Fact]
    public async Task A_tool_Kontena_cannot_fetch_is_never_asked_about()
    {
        var source = new Source("v9.9.9");
        var unfetchable = KnownTools.Kind with { Release = null };

        Assert.Null(await new ToolUpdateCheck(source, EmptyStore()).CheckAsync(unfetchable, "v0.31.0", Now));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_version_that_could_not_be_read_is_not_called_out_of_date()
    {
        var check = new ToolUpdateCheck(new Source("v0.32.0"), EmptyStore());

        // A tool that would not say what it is is already reported as Unusable. Calling it outdated on
        // top of that would be a second, wronger explanation of the same fact.
        Assert.Equal(new ToolUpdate("v0.32.0", IsNewer: false), await check.CheckAsync(KnownTools.Kind, null, Now));
    }
}
