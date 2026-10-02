using Kontena.Adapters.LocalClusters;
using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;
using Kontena.Sdk.Tooling.Fakes;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>The CNI field in the create form (KON-465) — shown, defaulted and folded into the spec.</summary>
public class NewClusterCniTests
{
    private static ProvisionerChoiceViewModel Choice(IClusterProvisioner provisioner) =>
        new(
            provisioner,
            new ToolReadiness(
                new ExternalTool("fake", "fake", ["version"], []),
                ToolState.Ready, "/fake/bin/fake", "v1.0.0", false, null),
            "For testing the form without a cluster tool.");

    /// <summary>A tool that wires its own network in and is never asked — minikube's answer.</summary>
    private static ProvisionerChoiceViewModel Silent(string id = "quiet") =>
        Choice(new FakeClusterProvisioner
        {
            Provisioner = id,
            Capabilities = new ProvisionerCapabilities { KubernetesVersion = true },
        });

    private static ProvisionerChoiceViewModel Kind() =>
        Choice(new KindClusterProvisioner(new FakeToolRunner()));

    private static NewClusterViewModel Form(params ProvisionerChoiceViewModel[] choices) =>
        new(choices, [LocalClusterRuntime.Docker, LocalClusterRuntime.Podman]);

    [Fact]
    public void A_tool_that_wires_its_own_network_in_is_not_asked_about_it()
    {
        var form = Form(Silent());
        form.Name = "dev";

        Assert.False(form.ShowCni);
        Assert.Null(form.Build()!.Cni);
    }

    [Fact]
    public void Kind_offers_its_own_first_and_that_choice_asks_for_nothing()
    {
        var form = Form(Kind());
        form.Name = "dev";

        Assert.True(form.ShowCni);
        Assert.Equal(KindCnis.Kindnet, form.Cni);

        // The default entry means "leave it to the tool", which is null in the spec rather than its name.
        Assert.Null(form.Build()!.Cni);
    }

    [Fact]
    public void Choosing_another_one_reaches_the_spec_and_the_previews()
    {
        var form = Form(Kind());
        form.Name = "dev";
        form.Cni = KindCnis.Cilium;

        Assert.Equal(KindCnis.Cilium, form.Build()!.Cni);

        // The two things the form promises to show before anything runs (KON-76).
        Assert.Contains("disableDefaultCNI: true", form.ConfigPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("--wait", form.CommandPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_to_a_tool_that_has_never_heard_of_the_choice_falls_back_to_its_default()
    {
        var kind = Kind();
        var other = Silent("minikube");
        var form = Form(kind, other);

        form.Cni = KindCnis.Calico;
        form.SelectProvisionerCommand.Execute(other);

        Assert.False(form.ShowCni);
        Assert.Equal(string.Empty, form.Cni);

        form.SelectProvisionerCommand.Execute(kind);
        Assert.Equal(KindCnis.Kindnet, form.Cni);
    }
}
