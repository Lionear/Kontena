using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// A ConfigMap or Secret opens a detail like every other kind does (KON-330), and that detail can
/// answer the question the list never could: is anything still using this?
/// </summary>
public sealed class ConfigDetailTests
{
    private static async Task<(ClusterSecretsViewModel Page, FakeClusterEngine Cluster)> SecretsAsync()
    {
        var cluster = new FakeClusterEngine();
        var page = new ClusterSecretsViewModel(cluster, "app");
        await page.LoadAsync();
        return (page, cluster);
    }

    private static async Task<ClusterConfigDetailViewModel> DetailAsync(string secret)
    {
        var (page, cluster) = await SecretsAsync();
        var detail = new ClusterConfigDetailViewModel(cluster, page.Items.Single(r => r.Name == secret));

        // The used-by list loads off the constructor.
        for (var i = 0; i < 100 && detail.PodsLoading; i++)
            await Task.Delay(5);

        return detail;
    }

    [Fact]
    public async Task A_row_asks_the_shell_to_open_it_rather_than_unfolding_in_place()
    {
        // The expander is gone (KON-330): one place a value can appear, not two.
        var (page, _) = await SecretsAsync();
        ConfigObjectRow? opened = null;
        page.RequestOpenDetail = row => opened = row;

        var row = page.Items.Single(r => r.Name == "app-tls");
        Assert.True(row.CanOpen);
        row.OpenCommand.Execute(null);

        Assert.Same(row, opened);
    }

    [Fact]
    public async Task The_detail_carries_the_keys_the_listing_already_read()
    {
        var detail = await DetailAsync("postgres-credentials");

        Assert.Equal(["password", "username"], detail.Keys.Select(k => k.Name).Order());

        // Carried, not fetched: the values are still not here until each key is asked.
        Assert.All(detail.Keys, key => Assert.Null(key.Value));
    }

    /// <summary>
    /// Used by is a scan of what the pods declare, not a label-selector match — nothing labels a pod
    /// with the secrets it mounts, and this is the question you actually have on a secret's page.
    /// </summary>
    [Fact]
    public async Task Used_by_finds_every_pod_that_reads_it_however_it_reads_it()
    {
        var detail = await DetailAsync("postgres-credentials");

        // Three api pods read it as environment; postgres-0 mounts it.
        Assert.Equal(
            ["api-7d9c", "api-7d9d", "api-7d9e", "postgres-0"],
            detail.Pods.Select(p => p.Name).Order());

        // Both hows named, and the noun agreeing in each — "1 pod mount it" is what phrasing this as a
        // verb produced.
        Assert.Contains("mounted by 1 pod", detail.UsedBySummary, StringComparison.Ordinal);
        Assert.Contains("read as environment by 3 pods", detail.UsedBySummary, StringComparison.Ordinal);
    }

    /// <summary>A registry credential is used by the kubelet, not by a container — the one use with no
    /// container behind it, and it still counts as in use.</summary>
    [Fact]
    public async Task A_pull_secret_counts_as_used()
    {
        var detail = await DetailAsync("ghcr-pull");

        Assert.Equal(3, detail.Pods.Count);
        Assert.Contains("used to pull images by 3 pods", detail.UsedBySummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_using_it_is_said_rather_than_shown_as_an_empty_list()
    {
        // An empty list on its own reads as "nothing is running", which is a different answer.
        var cluster = new FakeClusterEngine();
        var page = new ClusterConfigMapsViewModel(cluster, "app");
        await page.LoadAsync();

        var detail = new ClusterConfigDetailViewModel(cluster, page.Items.Single(r => r.Name == "kube-root-ca.crt"));
        for (var i = 0; i < 100 && detail.PodsLoading; i++)
            await Task.Delay(5);

        Assert.Empty(detail.Pods);
        Assert.Equal(string.Empty, detail.UsedBySummary);
        Assert.Contains("No pod", detail.PodsEmptyReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same object twice is one window, not two (KON-308) — the key has to survive a list reload
    /// handing the detail a brand new row for the same secret.
    /// </summary>
    [Fact]
    public async Task Two_details_for_the_same_secret_share_a_key()
    {
        var first = await DetailAsync("app-tls");
        var second = await DetailAsync("app-tls");

        Assert.Equal(first.DetailKey, second.DetailKey);
        Assert.NotEqual(first.DetailKey, (await DetailAsync("postgres-credentials")).DetailKey);
    }

    [Fact]
    public async Task A_secret_says_it_is_one_and_a_config_map_does_not_claim_a_type()
    {
        var secret = await DetailAsync("app-tls");

        Assert.True(secret.IsSecret);
        Assert.True(secret.HasType);
        Assert.Equal("kubernetes.io/tls", secret.TypeText);

        var cluster = new FakeClusterEngine();
        var page = new ClusterConfigMapsViewModel(cluster, "app");
        await page.LoadAsync();
        var configMap = new ClusterConfigDetailViewModel(cluster, page.Items.First(r => r.Name == "web-config"));

        Assert.False(configMap.IsSecret);
        Assert.False(configMap.HasType);
    }
}
