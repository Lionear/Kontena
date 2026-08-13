using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Versioning;

namespace Kontena.App.Tests;

/// <summary>
/// The cluster overview already showed the version; now it shows the verdict beside it (KON-371).
/// <para>
/// The point of these tests is the separation the ticket insisted on. The page draws two warnings that
/// look identical — the same icon, the same amber — and they answer different questions: node skew asks
/// whether the parts of this cluster agree with each other, support asks whether anyone still repairs
/// this release. One can be true while the other is false, and the page has to be able to say so.
/// </para>
/// </summary>
public sealed class ClusterOverviewSupportTests : IDisposable
{
    private readonly string _cache = Path.Combine(
        Path.GetTempPath(), $"kontena-overview-support-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }

    /// <summary>
    /// The fake cluster runs v1.29.4 and calls itself GKE, so a calendar keyed on the managed product
    /// is the only one that can answer it — which is also how a test notices if the page starts asking
    /// upstream instead.
    /// </summary>
    private sealed class Calendar(bool maintained) : IReleaseCalendar
    {
        public List<string> Asked { get; } = [];

        public ValueTask<IReadOnlyList<ReleaseCycle>?> CyclesAsync(
            string product, CancellationToken ct = default)
        {
            Asked.Add(product);

            // Latest is what the cluster already runs, so the only thing under test here is the support
            // window — a newer patch is its own sentence and would drown this one out.
            return ValueTask.FromResult<IReadOnlyList<ReleaseCycle>?>(
                product == "google-kubernetes-engine"
                    ? [new("1.29", maintained, new DateOnly(2026, 2, 27), Latest: "1.29.4")]
                    : []);
        }
    }

    private static async Task<ClusterOverviewViewModel> SettledAsync(ClusterOverviewViewModel page)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (page.Support is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return page;
    }

    [Fact]
    public async Task A_cluster_on_a_dropped_release_says_so_beside_its_version()
    {
        var calendar = new Calendar(maintained: false);
        using var page = await SettledAsync(new ClusterOverviewViewModel(
            new FakeClusterEngine(), new VersionSupportCheck(calendar, _cache)));

        Assert.Equal("google-kubernetes-engine", Assert.Single(calendar.Asked));
        Assert.True(page.HasSupportWarning);
        Assert.Equal("Release 1.29 has not been supported since 27 February 2026.", page.SupportDetail);
    }

    /// <summary>
    /// The one that would break if the two warnings were ever folded into one flag: this cluster's
    /// nodes are three minors behind its apiserver, and its release is still supported. Both statements
    /// are true at once, and each belongs to a different row.
    /// </summary>
    [Fact]
    public async Task Node_skew_and_release_support_do_not_answer_for_each_other()
    {
        using var page = await SettledAsync(new ClusterOverviewViewModel(
            new FakeClusterEngine(), new VersionSupportCheck(new Calendar(maintained: true), _cache)));

        Assert.False(page.HasSupportWarning);
        Assert.Equal(string.Empty, page.SupportDetail);
        Assert.Contains(page.Nodes, n => n.HasVersionWarning);
    }

    [Fact]
    public void A_cluster_with_no_calendar_to_ask_says_nothing()
    {
        // How every existing caller builds this page in tests, and how the app builds it when the
        // release-calendar check was never wired up. Silence, not a crash and not a guess.
        using var page = new ClusterOverviewViewModel(new FakeClusterEngine());

        Assert.Null(page.Support);
        Assert.False(page.HasSupportWarning);
    }
}
