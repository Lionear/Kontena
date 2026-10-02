using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The detail page of a custom resource (KON-483): the same tabbed page a Deployment has, built from
/// what any object says about itself. The fake serves two cert-manager Certificates, one Ready and one
/// not, which is the pair a page that reads conditions has to tell apart.
/// </summary>
public sealed class CustomResourceDetailTests
{
    private static readonly GroupVersionKind Certificate = new("cert-manager.io", "v1", "Certificate");

    private static async Task<ClusterCustomResourceDetailViewModel> OpenAsync(
        string name, FakeClusterEngine? cluster = null, Action<ResourceRef>? onOpen = null)
    {
        var page = new ClusterCustomResourceDetailViewModel(
            cluster ?? new FakeClusterEngine(), new ResourceRef(Certificate, "default", name), onOpen: onOpen);

        for (var i = 0; i < 200 && (page.IsLoading || !page.UsersChecked || page.PodsLoading); i++)
            await Task.Delay(10);

        return page;
    }

    [Fact]
    public async Task The_overview_shows_the_columns_its_crd_declares_and_its_status()
    {
        using var page = await OpenAsync("kontena-app-tls");

        // The printer columns, minus Name (the title) and Age (the header), then the status fields.
        Assert.Equal(
            ["Ready", "Secret", "notAfter", "revision"],
            page.Fields.Select(f => f.Name));
        Assert.Equal("True", page.Fields[0].Value);
        Assert.NotEqual("—", page.AgeText);
        Assert.Null(page.LoadNote);
    }

    [Fact]
    public async Task Conditions_are_shown_and_coloured_by_whether_they_are_healthy()
    {
        using var ready = await OpenAsync("kontena-app-tls");
        using var failing = await OpenAsync("kontena-api-tls");

        var good = Assert.Single(ready.Conditions);
        Assert.Equal("Ready", good.Type);
        Assert.Equal("True", good.State);
        Assert.Equal("#ff34d399", good.Brush.ToString(), ignoreCase: true);

        var bad = Assert.Single(failing.Conditions);
        Assert.Equal("False", bad.State);
        Assert.Contains("Secret does not exist", bad.Detail, StringComparison.Ordinal);
        Assert.Equal("#fff5b14c", bad.Brush.ToString(), ignoreCase: true);
    }

    /// <summary>Some conditions are named for trouble, and for those True is the bad answer.</summary>
    [Theory]
    [InlineData("Degraded", "True", "#fff5b14c")]
    [InlineData("Degraded", "False", "#ff34d399")]
    [InlineData("Available", "False", "#fff5b14c")]
    [InlineData("Ready", "Unknown", "#ff5c6675")]
    public void A_condition_named_for_trouble_reads_the_other_way_round(string type, string status, string colour)
    {
        var row = new ResourceConditionRow(new ResourceCondition(type, status, "", "", null));

        Assert.Equal(colour, row.Brush.ToString(), ignoreCase: true);
    }

    [Fact]
    public async Task Labels_and_annotations_are_listed()
    {
        using var page = await OpenAsync("kontena-app-tls");

        Assert.Contains(page.Labels, l => l.Name == "app.kubernetes.io/name" && l.Value == "kontena");
        Assert.Contains(page.Annotations, a => a.Name == "cert-manager.io/issuer-name");
    }

    /// <summary>The cross-link (KON-455), on the page where the object now lives.</summary>
    [Fact]
    public async Task It_says_which_workloads_use_it_and_how_that_was_found()
    {
        using var page = await OpenAsync("kontena-app-tls");

        Assert.True(page.HasUsers);
        Assert.False(page.NothingUsesIt);

        var mounts = page.Users.Single(u => u.Name == "kontena-web");
        Assert.Equal("mounts Secret kontena-app-tls", mounts.Evidence);
        Assert.Equal("uses this", mounts.Direction);
        Assert.Equal("Deployment · namespace default", mounts.Where);

        var owned = page.Users.Single(u => u.Name == "kontena-api");
        Assert.Equal("ownerReference", owned.Evidence);
        Assert.Equal("created by this", owned.Direction);
    }

    [Fact]
    public async Task Clicking_a_user_hands_the_shell_the_workload_to_open()
    {
        ResourceRef? opened = null;
        using var page = await OpenAsync("kontena-app-tls", onOpen: target => opened = target);

        page.Users.Single(u => u.Name == "kontena-web").OpenCommand.Execute(null);

        Assert.Equal("kontena-web", opened?.Name);
        Assert.Equal(GroupVersionKind.Deployment, opened?.Kind);
    }

    /// <summary>
    /// Nothing found is stated, not left blank: a generic ladder will miss a custom resource that links
    /// itself some other way, and silence cannot be told apart from "nothing uses this".
    /// </summary>
    [Fact]
    public async Task An_object_nothing_uses_says_so()
    {
        using var page = await OpenAsync("kontena-api-tls");

        Assert.False(page.HasUsers);
        Assert.True(page.NothingUsesIt);
    }

    [Fact]
    public async Task An_object_with_no_pods_says_why_the_tab_is_empty()
    {
        using var page = await OpenAsync("kontena-api-tls");

        Assert.False(page.HasPods);
        Assert.Contains("controlled by this object", page.PodsEmptyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_object_that_is_not_there_says_so_instead_of_drawing_an_empty_page()
    {
        using var page = await OpenAsync("never-existed");

        Assert.False(page.HasFields);
        Assert.Contains("no such object", page.LoadNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page follows its object (KON-483): a status is the reason to open it, and a status read once
    /// is the stale "Ready 1" the ticket was opened over. Driven through the watch, because that the
    /// generic watch reaches this page at all is the new part.
    /// </summary>
    [Fact]
    public async Task A_change_to_the_object_is_read_again()
    {
        var cluster = new FakeClusterEngine();
        using var page = await OpenAsync("kontena-app-tls", cluster);
        var before = cluster.CallsTo(nameof(FakeClusterEngine.GetObjectAsync));

        cluster.EmitWatchEvent(new ResourceEvent
        {
            Type = WatchEventType.Modified,
            Resource = new ResourceRef(Certificate, "default", "kontena-app-tls"),
        });

        for (var i = 0; i < 200 && cluster.CallsTo(nameof(FakeClusterEngine.GetObjectAsync)) == before; i++)
            await Task.Delay(10);

        Assert.True(cluster.CallsTo(nameof(FakeClusterEngine.GetObjectAsync)) > before, "the object was not read again");
    }
}
