using Kontena.Core.Versioning;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Telling someone their engine runs a release nobody maintains any more, without Kontena claiming to
/// be the authority on that — and without asking the network every time a page opens (KON-370).
/// </summary>
public class VersionSupportCheckTests
{
    private static string EmptyRoot() =>
        Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}");

    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Docker Engine as endoflife.date described it on 2026-08-11.</summary>
    private static ReleaseCycle[] DockerEngine =>
    [
        new("29", IsMaintained: true, EolFrom: null, Latest: "29.7.2"),
        new("28", IsMaintained: false, EolFrom: new DateOnly(2026, 5, 13), Latest: "28.5.2"),
        new("27", IsMaintained: false, EolFrom: new DateOnly(2025, 5, 3), Latest: "27.5.1"),
    ];

    /// <summary>Counts its calls, because "how often do we ask" is what the cache is for.</summary>
    private sealed class Calendar(IReadOnlyList<ReleaseCycle>? cycles, Exception? throws = null) : IReleaseCalendar
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(string product, CancellationToken ct = default)
        {
            Calls++;

            if (throws is not null)
                throw throws;

            return ValueTask.FromResult(cycles);
        }
    }

    private static VersionSupportCheck Check(IReleaseCalendar calendar) => new(calendar, EmptyRoot());

    [Fact]
    public async Task A_release_nobody_maintains_any_more_is_a_problem()
    {
        var support = await Check(new Calendar(DockerEngine)).CheckAsync("docker-engine", "28.5.2", Now);

        Assert.NotNull(support);
        Assert.True(support.IsProblem);
        Assert.Equal("28", support.Cycle);
        Assert.Equal(new DateOnly(2026, 5, 13), support.EolFrom);
    }

    [Fact]
    public async Task A_maintained_release_is_not_news()
    {
        var support = await Check(new Calendar(DockerEngine)).CheckAsync("docker-engine", "29.7.2", Now);

        Assert.NotNull(support);
        Assert.False(support.IsProblem);
    }

    [Fact]
    public async Task A_newer_patch_in_the_same_cycle_is_named()
    {
        var support = await Check(new Calendar(DockerEngine)).CheckAsync("docker-engine", "29.1.0", Now);

        // The second signal the same document already carries: still supported, but behind on patches.
        Assert.Equal("29.7.2", support?.NewerPatch);
    }

    [Fact]
    public async Task Being_on_the_newest_patch_names_none()
    {
        var support = await Check(new Calendar(DockerEngine)).CheckAsync("docker-engine", "29.7.2", Now);

        Assert.Null(support?.NewerPatch);
    }

    [Fact]
    public async Task The_most_specific_cycle_wins()
    {
        // containerd publishes both a 2.x and a 2.1.x line. Matching the shorter one first would put a
        // 2.1 install in a cycle with someone else's support dates.
        ReleaseCycle[] containerd =
        [
            new("2", IsMaintained: true, EolFrom: null, Latest: "2.9.9"),
            new("2.1", IsMaintained: false, EolFrom: new DateOnly(2026, 7, 3), Latest: "2.1.9"),
        ];

        var support = await Check(new Calendar(containerd)).CheckAsync("containerd", "v2.1.9", Now);

        Assert.Equal("2.1", support?.Cycle);
        Assert.True(support?.IsProblem);
    }

    [Fact]
    public async Task A_vendor_suffix_does_not_hide_the_version()
    {
        ReleaseCycle[] kubernetes =
        [
            new("1.34", IsMaintained: true, EolFrom: new DateOnly(2026, 10, 27), Latest: "1.34.10"),
            new("1.33", IsMaintained: false, EolFrom: new DateOnly(2026, 6, 28), Latest: "1.33.13"),
        ];

        var support = await Check(new Calendar(kubernetes)).CheckAsync("kubernetes", "v1.33.4-gke.1043000", Now);

        Assert.Equal("1.33", support?.Cycle);
    }

    [Fact]
    public async Task A_release_the_calendar_does_not_know_says_nothing()
    {
        // Newer than anything published — a nightly, or a calendar that has not caught up. Kontena is
        // not the vendor and will not guess on their behalf.
        Assert.Null(await Check(new Calendar(DockerEngine)).CheckAsync("docker-engine", "30.0.1", Now));
    }

    [Fact]
    public async Task A_version_that_could_not_be_read_is_never_asked_about()
    {
        var calendar = new Calendar(DockerEngine);

        Assert.Null(await Check(calendar).CheckAsync("docker-engine", "unknown", Now));
        Assert.Equal(0, calendar.Calls);
    }

    [Fact]
    public async Task A_backend_with_no_product_is_never_asked_about()
    {
        var calendar = new Calendar(DockerEngine);

        // Apple's `container` is not published anywhere we can read. Silence, not a guess.
        Assert.Null(await Check(calendar).CheckAsync(null, "1.2.3", Now));
        Assert.Equal(0, calendar.Calls);
    }

    [Fact]
    public async Task The_answer_is_asked_for_once_a_day()
    {
        var calendar = new Calendar(DockerEngine);
        var root = EmptyRoot();

        await new VersionSupportCheck(calendar, root).CheckAsync("docker-engine", "28.5.2", Now);
        await new VersionSupportCheck(calendar, root).CheckAsync("docker-engine", "28.5.2", Now.AddHours(23));

        Assert.Equal(1, calendar.Calls);
    }

    [Fact]
    public async Task After_a_day_it_asks_again()
    {
        var calendar = new Calendar(DockerEngine);
        var root = EmptyRoot();

        await new VersionSupportCheck(calendar, root).CheckAsync("docker-engine", "28.5.2", Now);
        await new VersionSupportCheck(calendar, root).CheckAsync("docker-engine", "28.5.2", Now.AddHours(25));

        Assert.Equal(2, calendar.Calls);
    }

    [Fact]
    public async Task A_clock_that_went_backwards_does_not_pin_a_stale_answer()
    {
        var calendar = new Calendar(DockerEngine);
        var root = EmptyRoot();

        await new VersionSupportCheck(calendar, root).CheckAsync("docker-engine", "28.5.2", Now);
        await new VersionSupportCheck(calendar, root).CheckAsync("docker-engine", "28.5.2", Now.AddDays(-3));

        Assert.Equal(2, calendar.Calls);
    }

    [Fact]
    public async Task A_cached_answer_still_answers_when_the_calendar_has_gone_away()
    {
        var root = EmptyRoot();

        await new VersionSupportCheck(new Calendar(DockerEngine), root).CheckAsync("docker-engine", "28.5.2", Now);

        var offline = new VersionSupportCheck(
            new Calendar(null, new HttpRequestException("no route")), root);

        // What it knew yesterday is still true today; EOL dates do not move backwards.
        Assert.True((await offline.CheckAsync("docker-engine", "28.5.2", Now.AddHours(1)))?.IsProblem);
    }

    [Fact]
    public async Task Being_offline_says_nothing_rather_than_something_wrong()
    {
        var offline = new Calendar(null, new HttpRequestException("no route"));

        // An unknown is not a warning.
        Assert.Null(await Check(offline).CheckAsync("docker-engine", "28.5.2", Now));
    }
}
