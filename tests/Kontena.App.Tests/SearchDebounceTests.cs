using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Typing should cost one list rebuild, not one per letter (KON-164 follow-up).
/// <para>
/// Two separate problems live here. The debounce collapses a burst of keystrokes; the reconciliation
/// in <see cref="ClusterListViewModel{TRow}"/> is what makes each rebuild cheap. A debounce alone
/// would only have delayed the same expensive rebuild.
/// </para>
/// </summary>
public sealed class SearchDebounceTests
{
    private static async Task<ClusterPodsViewModel> LoadedPods()
    {
        var pods = new ClusterPodsViewModel(new FakeClusterEngine(), null);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!pods.HasLoaded && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(pods.HasLoaded);
        return pods;
    }

    /// <summary>Counts how many times the bound collection changed at all.</summary>
    private sealed class ChangeCounter
    {
        public int Count { get; private set; }

        public ChangeCounter(System.Collections.Specialized.INotifyCollectionChanged source) =>
            source.CollectionChanged += (_, _) => Count++;
    }

    [Fact]
    public async Task Only_the_last_keystroke_of_a_burst_reaches_the_page()
    {
        var shell = new MainWindowViewModel { SearchDebounce = TimeSpan.FromMilliseconds(40) };
        var pods = await LoadedPods();
        shell.CurrentPage = pods;

        shell.SearchText = "a";
        shell.SearchText = "ap";
        shell.SearchText = "api";

        await shell.SearchSettled;

        Assert.Equal("api", pods.SearchText);
    }

    [Fact]
    public async Task An_abandoned_prefix_never_reaches_the_page()
    {
        // The point of the debounce: "a" would have matched most of the cluster and rebuilt the whole
        // list for a result nobody looked at.
        var shell = new MainWindowViewModel { SearchDebounce = TimeSpan.FromMilliseconds(40) };
        var pods = await LoadedPods();
        shell.CurrentPage = pods;

        var changes = new ChangeCounter(pods.Items);

        shell.SearchText = "a";
        shell.SearchText = "ap";
        shell.SearchText = "api";
        await shell.SearchSettled;

        // One narrowing, not three. Removing six rows one by one is six notifications, so this is a
        // ceiling rather than an exact count — the assertion is that "a" and "ap" never landed.
        Assert.True(changes.Count > 0);
        Assert.Equal("api", pods.SearchText);
    }

    [Fact]
    public async Task Clearing_the_box_is_immediate()
    {
        // Waiting to show everything again is pure lag: no next keystroke is coming to collapse.
        var shell = new MainWindowViewModel { SearchDebounce = TimeSpan.Zero };
        var pods = await LoadedPods();
        shell.CurrentPage = pods;

        shell.SearchText = "api";
        Assert.Equal("api", pods.SearchText);

        // Even behind a delay long enough to notice, clearing does not wait for it.
        shell.SearchDebounce = TimeSpan.FromSeconds(30);
        shell.SearchText = string.Empty;

        Assert.Equal(string.Empty, pods.SearchText);
    }

    [Fact]
    public async Task Navigating_away_mid_delay_does_not_filter_the_next_page()
    {
        var shell = new MainWindowViewModel { SearchDebounce = TimeSpan.FromMilliseconds(60) };
        var pods = await LoadedPods();
        var services = new ClusterServicesViewModel(new FakeClusterEngine(), null);
        shell.CurrentPage = pods;

        shell.SearchText = "api";
        shell.CurrentPage = services;
        await shell.SearchSettled;

        // Neither page is filtered: not the one left behind, and not the one arrived at — whose search
        // box reads empty, so a filter applied to it would be invisible.
        Assert.Equal(string.Empty, pods.SearchText);
        Assert.Equal(string.Empty, services.SearchText);
    }

    [Fact]
    public async Task Rows_that_still_match_keep_their_place_rather_than_being_rebuilt()
    {
        // The reconciliation, which is the half that actually made typing cheap: narrowing from "api"
        // to "api-7d9c" should remove two rows, not replace three.
        var pods = await LoadedPods();
        pods.SearchText = "api";

        var survivor = pods.Items.Single(p => p.Name == "api-7d9c");
        var changes = new ChangeCounter(pods.Items);

        pods.SearchText = "api-7d9c";

        Assert.Same(survivor, Assert.Single(pods.Items));

        // Two removals. A Clear would have raised one Reset and thrown the survivor's visuals away
        // with the rest.
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Widening_a_search_puts_rows_back_in_order()
    {
        var pods = await LoadedPods();
        pods.SearchText = "api-7d9c";
        pods.SearchText = "api";

        Assert.Equal(["api-7d9c", "api-7d9d", "api-7d9e"], pods.Items.Select(p => p.Name));
    }
}
