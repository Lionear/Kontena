using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Core.Tooling;
using Kontena.Core.Tooling.Fakes;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The create form's version list, which belongs to the chosen tool rather than to the form (KON-144).
/// Two provisioners that disagree about what exists is not a contrived case: it is kind and minikube
/// as they are today.
/// </summary>
public class NewClusterVersionsTests
{
    private static ManagedToolStore EmptyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"kontena-tests-{Guid.NewGuid():N}"));

    /// <summary>Stands in for kind: a maintained list, no default it can name, and a node image field.</summary>
    private static FakeClusterProvisioner KindLike() => new()
    {
        Provisioner = "kind-like",
        DisplayName = "kind-like",
        Versions = new ClusterVersionOptions(["v1.36.1", "v1.35.5"]),
        Capabilities = new ProvisionerCapabilities { KubernetesVersion = true, NodeImage = true },
    };

    /// <summary>Stands in for minikube: its own answer, a default it can name, and no node image.</summary>
    private static FakeClusterProvisioner MinikubeLike() => new()
    {
        Provisioner = "minikube-like",
        DisplayName = "minikube-like",
        Versions = new ClusterVersionOptions(["v1.35.1", "v1.34.4"], "v1.35.1"),
        Capabilities = new ProvisionerCapabilities { KubernetesVersion = true },
    };

    private static async Task<NewClusterViewModel> FormAsync(params FakeClusterProvisioner[] provisioners)
    {
        var page = new LocalClustersViewModel(provisioners, new FakeToolRunner(), store: EmptyStore());
        await page.LoadAsync();
        page.NewClusterCommand.Execute(null);

        return page.Form!;
    }

    [Fact]
    public async Task The_list_is_the_chosen_tools_own()
    {
        var form = await FormAsync(KindLike());

        Assert.Equal(["Default for this release", "v1.36.1", "v1.35.5"], form.Versions);
    }

    [Fact]
    public async Task A_tool_that_names_its_default_gets_it_named()
    {
        var form = await FormAsync(MinikubeLike());

        // "Default" alone leaves someone to guess which version that is; this tool told us.
        Assert.Equal("Default (v1.35.1)", form.DefaultVersion);
        Assert.Equal("Default (v1.35.1)", form.Version);
    }

    [Fact]
    public async Task Switching_tools_swaps_the_list_and_drops_a_version_the_other_cannot_boot()
    {
        var form = await FormAsync(KindLike(), MinikubeLike());

        form.Version = "v1.36.1";
        form.SelectProvisionerCommand.Execute(form.Provisioners[1]);

        // The version that was picked does not exist for this tool, so it falls back to its default
        // rather than leaving a dropdown showing something it would reject.
        Assert.Equal(["Default (v1.35.1)", "v1.35.1", "v1.34.4"], form.Versions);
        Assert.Equal("Default (v1.35.1)", form.Version);
    }

    [Fact]
    public async Task A_version_both_tools_have_survives_the_switch()
    {
        var kind = KindLike();
        var form = await FormAsync(
            new FakeClusterProvisioner
            {
                Provisioner = "other",
                DisplayName = "Other",
                Versions = new ClusterVersionOptions(["v1.35.5"]),
                Capabilities = new ProvisionerCapabilities { KubernetesVersion = true },
            },
            kind);

        form.Version = "v1.35.5";
        form.SelectProvisionerCommand.Execute(form.Provisioners[1]);

        // Resetting what did not need resetting would throw away a deliberate choice.
        Assert.Equal("v1.35.5", form.Version);
    }

    [Fact]
    public async Task The_default_entry_asks_for_no_version_at_all()
    {
        var form = await FormAsync(MinikubeLike());
        form.Name = "dev";

        Assert.Null(form.Build()!.KubernetesVersion);
    }

    [Fact]
    public async Task A_chosen_version_reaches_the_spec()
    {
        var form = await FormAsync(KindLike());
        form.Name = "dev";
        form.Version = "v1.36.1";

        Assert.Equal("v1.36.1", form.Build()!.KubernetesVersion);
    }

    [Fact]
    public async Task The_node_image_field_is_only_where_the_list_cannot_be_complete()
    {
        Assert.True((await FormAsync(KindLike())).ShowNodeImage);
        Assert.False((await FormAsync(MinikubeLike())).ShowNodeImage);
    }

    [Fact]
    public async Task A_node_image_typed_for_one_tool_is_not_sent_to_another()
    {
        var form = await FormAsync(KindLike(), MinikubeLike());
        form.Name = "dev";
        form.NodeImage = "kindest/node:v1.36.1";

        Assert.Equal("kindest/node:v1.36.1", form.Build()!.NodeImage);

        form.SelectProvisionerCommand.Execute(form.Provisioners[1]);

        // Same discipline as every other field the tool cannot honour: left out, not sent and rejected.
        Assert.Null(form.Build()!.NodeImage);
    }

    [Fact]
    public async Task A_tool_that_would_not_say_still_offers_its_default()
    {
        var form = await FormAsync(new FakeClusterProvisioner
        {
            Versions = ClusterVersionOptions.None,
            Capabilities = new ProvisionerCapabilities { KubernetesVersion = true },
        });

        // An absent or unreadable tool must not leave an empty dropdown: its own default always works.
        Assert.Equal(["Default for this release"], form.Versions);
    }
}
