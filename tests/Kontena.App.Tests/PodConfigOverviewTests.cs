using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// The pod Overview tab (KON-390): what the pod runs, and which ConfigMaps and Secrets it reads.
/// Both are read off the pod that is already on the page — the keys behind a row are the only thing
/// that costs a call, and only once asked for.
/// </summary>
public sealed class PodConfigOverviewTests
{
    private static readonly TerminalFont Font = new("JetBrains Mono", 13, false);

    private static async Task<ClusterPodDetailViewModel> PageFor(string pod)
    {
        var cluster = new FakeClusterEngine();
        var pods = await cluster.ListPodsAsync("app");
        return new ClusterPodDetailViewModel(cluster, pods.First(p => p.Name == pod), Font);
    }

    private static async Task<PodConfigRow> OpenedRow(ClusterPodDetailViewModel page, string name)
    {
        var row = page.ConfigRows.Single(r => r.Name == name);
        await row.ToggleCommand.ExecuteAsync(null);
        return row;
    }

    /// <summary>A ConfigMap's values are shown without asking, off the constructor of each key row.</summary>
    private static async Task SettleAsync(PodConfigRow row)
    {
        for (var i = 0; i < 100 && row.Keys.Any(k => k.Value is null && k.Error is null); i++)
            await Task.Delay(5);
    }

    [Fact]
    public async Task The_image_is_on_the_tab_you_land_on()
    {
        using var page = await PageFor("web-5f2a");

        Assert.Equal(["nginx:1.27-alpine"], page.Images);
        Assert.Equal("IMAGE", page.ImagesLabel);
        Assert.Contains("app=web", page.Labels);
    }

    /// <summary>
    /// An init container's image is not what the pod runs — it is what ran before it. Naming it here
    /// would answer "what is this pod" with the wrong image on exactly the pods that are stuck.
    /// </summary>
    [Fact]
    public async Task Init_images_stay_out_of_the_image_line()
    {
        using var page = await PageFor("migrate-9b4f");

        Assert.Equal(["ghcr.io/lionear/api:1.8"], page.Images);
    }

    [Fact]
    public async Task Every_object_the_pod_reads_is_listed_once_with_how_it_reads_it()
    {
        using var page = await PageFor("api-7d9c");

        Assert.Equal(["ghcr-pull", "postgres-credentials"], page.ConfigRows.Select(r => r.Name));
        Assert.All(page.ConfigRows, r => Assert.True(r.IsSecret));

        Assert.Equal("used to pull images", page.ConfigRows[0].UsageText);
        Assert.Equal("read as environment by c0", page.ConfigRows[1].UsageText);
    }

    [Fact]
    public async Task Config_maps_and_secrets_both_show_up_named_for_what_they_are()
    {
        using var page = await PageFor("web-5f2a");

        Assert.Equal(
            [("ConfigMap", "web-config"), ("Secret", "app-tls")],
            page.ConfigRows.Select(r => (r.KindLabel, r.Name)));
        Assert.All(page.ConfigRows, r => Assert.Equal("mounted as a volume", r.UsageText));
    }

    /// <summary>The section is built from the pod's own spec, so a pod that reads nothing gets none.</summary>
    [Fact]
    public async Task A_pod_that_reads_nothing_carries_no_section()
    {
        using var page = await PageFor("redis-0c1e");

        Assert.Empty(page.ConfigRows);
        Assert.False(page.HasConfigRows);
    }

    [Fact]
    public async Task A_secrets_keys_arrive_masked_and_only_show_when_the_eye_is_pressed()
    {
        using var page = await PageFor("api-7d9c");
        var row = await OpenedRow(page, "postgres-credentials");

        Assert.True(row.IsExpanded);
        Assert.Equal(["password", "username"], row.Keys.Select(k => k.Name).Order());

        // Opening the row names the keys; it does not put a value on screen.
        Assert.All(row.Keys, key => Assert.Null(key.Value));
        Assert.All(row.Keys, key => Assert.False(key.IsRevealed));

        // The tip is the button's accessible name, and it names the row: this page carries eyes in
        // two sections, and three buttons all called "Show the value" tell a screen reader nothing
        // (KON-56, KON-416).
        var password = row.Keys.Single(k => k.Name == "password");
        Assert.Equal("Show the value of password", password.RevealTip);

        await password.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("s3cr3t-but-not-really", password.Value);
        Assert.Equal("Hide the value of password", password.RevealTip);

        // Hiding drops it rather than folding it away — the other key never left the cluster at all.
        await password.ToggleCommand.ExecuteAsync(null);
        Assert.Null(password.Value);
        Assert.Null(row.Keys.Single(k => k.Name == "username").Value);
    }

    /// <summary>A ConfigMap has nothing to protect: pressing an eye on a LOG_LEVEL of "info" would
    /// teach the habit of pressing it without reading.</summary>
    [Fact]
    public async Task A_config_maps_values_are_simply_there()
    {
        using var page = await PageFor("web-5f2a");
        var row = await OpenedRow(page, "web-config");
        await SettleAsync(row);

        Assert.All(row.Keys, key => Assert.False(key.IsSecret));
        Assert.Equal("info", row.Keys.Single(k => k.Name == "LOG_LEVEL").Value);
    }

    [Fact]
    public async Task Closing_a_row_takes_the_revealed_value_with_it()
    {
        using var page = await PageFor("api-7d9c");
        var row = await OpenedRow(page, "postgres-credentials");

        await row.Keys.Single(k => k.Name == "password").ToggleCommand.ExecuteAsync(null);
        await row.ToggleCommand.ExecuteAsync(null);

        Assert.False(row.IsExpanded);
        Assert.Empty(row.Keys);
    }

    /// <summary>
    /// A pull secret the page may name but cannot read comes back with nothing in it. Saying so beats
    /// a row that unfolds onto a blank panel, which reads as a page that failed.
    /// </summary>
    [Fact]
    public async Task An_object_that_hands_over_no_keys_says_so()
    {
        using var page = await PageFor("api-7d9c");
        var row = await OpenedRow(page, "ghcr-pull");

        Assert.True(row.IsEmpty);
        Assert.False(row.HasKeys);
    }
}
