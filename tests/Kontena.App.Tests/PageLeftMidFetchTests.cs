using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration;

namespace Kontena.App.Tests;

/// <summary>
/// A read a page no longer needs is ended, not left running (KON-413).
/// <para>
/// The reported symptom was a hang while clicking between Pods and Deployments. Cluster pages are
/// rebuilt on every visit and reloaded on every settled watch event, and neither of those ended the
/// fetch already in flight — so every click left one cluster-wide read still running against a page
/// that could no longer draw it. They queued for the same connection pool and got slower as they
/// stacked (704 ms, 1517 ms, 2077 ms in the reported diagnostics), the working set climbed, and the
/// window stopped answering.
/// </para>
/// <para>
/// Tested on <see cref="ClusterListPageViewModel{TRow}"/> rather than on a page, because that is
/// where the fix is: every cluster list page inherits it, and a test per page would be seventeen
/// copies of one claim. What each page still owns is passing the token on to its own engine call —
/// the compiler asks it for one, and the reads below are the shape it has to keep.
/// </para>
/// </summary>
public sealed class PageLeftMidFetchTests
{
    /// <summary>
    /// A list page whose fetch is held open by the test, so there is something in flight to leave.
    /// Kindless on purpose: <c>StartWatching</c> is never called here, and a page that follows
    /// nothing is the load and the load only.
    /// </summary>
    private sealed class GatedPage(IClusterEngine cluster) : ClusterListPageViewModel<string>(cluster, null, null)
    {
        private readonly TaskCompletionSource _released = new();

        /// <summary>The token of the read that is out — the thing under test.</summary>
        public CancellationToken Token { get; private set; }

        /// <summary>Set once <see cref="LoadRowsAsync"/> has actually been entered.</summary>
        public TaskCompletionSource Started { get; } = new();

        /// <summary>What the held read answers with, once let go.</summary>
        public List<string> Rows { get; } = ["a", "b"];

        public void Release() => _released.TrySetResult();

        public override string SearchPlaceholder => "Search…";

        protected override async Task<IReadOnlyList<string>> LoadRowsAsync(CancellationToken ct)
        {
            Token = ct;
            Started.TrySetResult();

            await _released.Task.WaitAsync(ct);
            return Rows;
        }

        protected override bool Matches(string row, string term) => Contains(row, term);
    }

    private static async Task<GatedPage> LoadingAsync()
    {
        var page = new GatedPage(new FakeClusterEngine());
        _ = page.LoadAsync();
        await page.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return page;
    }

    [Fact]
    public async Task Leaving_a_page_ends_the_read_it_had_out()
    {
        var page = await LoadingAsync();
        Assert.False(page.Token.IsCancellationRequested);

        // What the shell does to the outgoing page on every cluster navigation.
        page.Dispose();

        Assert.True(page.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_read_that_answers_after_the_page_was_left_does_not_land_on_it()
    {
        var page = await LoadingAsync();
        page.Dispose();

        // The engine answering anyway is the ordinary case, not a strange one: cancelling a request
        // does not un-send it, and a fake has nothing to cancel at all.
        page.Release();
        await Task.Delay(50);

        Assert.Empty(page.Items);
        Assert.False(page.HasLoaded);
    }

    [Fact]
    public async Task A_reload_supersedes_the_read_still_out_rather_than_racing_it()
    {
        var page = await LoadingAsync();
        var first = page.Token;

        // A watch event arriving while the first read is still out — the other half of the pile-up,
        // and the one that happens without anybody clicking anything.
        _ = page.LoadAsync();

        Assert.True(first.IsCancellationRequested);
        Assert.False(page.Token.IsCancellationRequested);
        Assert.NotEqual(first, page.Token);

        page.Release();
        await Task.Delay(50);

        // The second read's answer, landing once rather than twice.
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task The_spinner_stays_up_while_the_read_that_replaced_the_first_is_still_out()
    {
        var page = await LoadingAsync();
        Assert.True(page.IsLoading);

        _ = page.LoadAsync();

        // The superseded load returning must not call the page loaded: it is still blank, and the
        // read that owns it has not answered yet.
        Assert.True(page.IsLoading);

        page.Release();
        await Task.Delay(50);

        Assert.False(page.IsLoading);
    }

    [Fact]
    public void A_page_load_hands_out_one_live_token_at_a_time()
    {
        var load = new PageLoad();

        var first = load.Begin();
        Assert.False(first.IsCancellationRequested);

        var second = load.Begin();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);

        load.Cancel();
        Assert.True(second.IsCancellationRequested);

        // Twice is a no-op rather than a throw: Dispose runs on a page that may never have loaded.
        load.Cancel();
    }
}

/// <summary>
/// One <c>navigate to</c> per navigation (KON-413).
/// <para>
/// Both <c>NavigateTo</c> and <c>NavigateCluster</c> marked the trace, and every cluster navigation
/// goes through both — so the diagnostics logged every sidebar click twice, a millisecond apart. That
/// reads exactly like a command firing twice, and the bug report it came with said so. The mark
/// belongs where every cluster navigation actually arrives, and nowhere above it.
/// </para>
/// </summary>
[Collection("Diag")]
public sealed class NavigationTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kontena-nav-{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "diagnostics.log");

    public void Dispose()
    {
        DiagLog.Close();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_cluster_navigation_is_logged_once()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        DiagLog.Open(LogPath);
        shell.NavigateCommand.Execute("pods");
        DiagLog.Close();

        var marks = File.ReadAllLines(LogPath)
            .Count(line => line.Contains("navigate to pods", StringComparison.Ordinal));

        Assert.Equal(1, marks);
    }
}
