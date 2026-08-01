using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;
using Xunit;

namespace Kontena.App.Tests;

public class NewClusterViewModelTests
{
    /// <summary>A form over one fake provisioner that can do everything kind can.</summary>
    private static NewClusterViewModel Form(ProvisionerCapabilities? capabilities = null)
    {
        var provisioner = new FakeClusterProvisioner
        {
            Capabilities = capabilities ?? new ProvisionerCapabilities
            {
                MultiNode = true,
                HighAvailability = true,
                PortMappings = true,
                IngressReady = true,
                KubernetesVersion = true,
                Runtimes = [LocalClusterRuntime.Docker, LocalClusterRuntime.Podman],
            },
        };

        var choice = new ProvisionerChoiceViewModel(
            provisioner,
            new ToolReadiness(
                new ExternalTool("fake", "fake", ["version"], []),
                ToolState.Ready, "/fake/bin/fake", "v1.0.0", false, null),
            "A fake, for testing forms without a cluster tool.");

        return new NewClusterViewModel(
            [choice], [LocalClusterRuntime.Docker, LocalClusterRuntime.Podman, LocalClusterRuntime.Kvm2]);
    }

    [Fact]
    public void An_empty_form_cannot_be_submitted_and_says_nothing_yet()
    {
        var form = Form();

        Assert.False(form.CanCreate);
        Assert.Null(form.NameProblem);
        Assert.False(form.HasContextPreview);
    }

    [Fact]
    public void A_bad_name_explains_which_rule_it_broke()
    {
        var form = Form();
        form.Name = "Dev Cluster";

        Assert.True(form.HasNameProblem);
        Assert.False(form.CanCreate);
        Assert.Contains("lowercase", form.NameProblem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_good_name_shows_the_context_it_will_write()
    {
        var form = Form();
        form.Name = "dev";

        Assert.True(form.CanCreate);
        Assert.Equal("kind-dev", form.ContextPreview);
    }

    [Fact]
    public void The_command_preview_follows_what_was_typed()
    {
        var form = Form();
        form.Name = "dev";

        Assert.Equal("kind create cluster --name dev --wait 300s", form.CommandPreview);
        Assert.False(form.HasConfigPreview);

        form.WorkerNodes = "2";

        Assert.Contains("--config", form.CommandPreview, StringComparison.Ordinal);
        Assert.True(form.HasConfigPreview);
        Assert.Contains("role: worker", form.ConfigPreview!, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_waiting_drops_the_wait_flag()
    {
        var form = Form();
        form.Name = "dev";
        form.WaitForReady = false;

        Assert.DoesNotContain("--wait", form.CommandPreview, StringComparison.Ordinal);
        Assert.Null(form.Build()!.ReadyTimeout);
    }

    [Fact]
    public void A_half_typed_port_blocks_the_create_rather_than_being_dropped()
    {
        var form = Form();
        form.Name = "dev";
        form.Ports[0].HostPort = "8080";

        Assert.True(form.HasPortProblem);
        Assert.False(form.CanCreate);

        form.Ports[0].NodePort = "80";

        Assert.False(form.HasPortProblem);
        Assert.True(form.CanCreate);
    }

    [Fact]
    public void An_untouched_port_row_is_ignored()
    {
        var form = Form();
        form.Name = "dev";

        Assert.True(form.CanCreate);
        Assert.Empty(form.Build()!.PortMappings);
    }

    [Fact]
    public void A_port_outside_the_range_is_not_a_port()
    {
        var form = Form();
        form.Name = "dev";
        form.Ports[0].HostPort = "70000";
        form.Ports[0].NodePort = "80";

        Assert.True(form.HasPortProblem);
    }

    [Fact]
    public void Removing_the_last_port_row_leaves_one_to_type_in()
    {
        var form = Form();
        form.Ports[0].RemoveCommand.Execute(null);

        Assert.Single(form.Ports);
        Assert.True(form.Ports[0].IsEmpty);
    }

    [Fact]
    public void Worker_count_has_to_be_a_number_in_range()
    {
        var form = Form();
        form.Name = "dev";

        form.WorkerNodes = "three";
        Assert.False(form.CanCreate);

        form.WorkerNodes = "99";
        Assert.False(form.CanCreate);

        form.WorkerNodes = "3";
        Assert.True(form.CanCreate);
        Assert.Equal(3, form.Build()!.WorkerNodes);
    }

    [Fact]
    public void The_default_version_asks_for_no_image_at_all()
    {
        var form = Form();
        form.Name = "dev";

        Assert.Null(form.Build()!.KubernetesVersion);

        form.Version = "v1.31.0";

        Assert.Equal("v1.31.0", form.Build()!.KubernetesVersion);
        Assert.Contains("kindest/node:v1.31.0", form.CommandPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Podman_reaches_the_spec_as_the_runtime()
    {
        var form = Form();
        form.Name = "dev";
        form.SelectRuntimeCommand.Execute("Podman");

        Assert.Equal(LocalClusterRuntime.Podman, form.Build()!.Runtime);
    }

    [Fact]
    public void Ingress_writes_the_label_and_installs_nothing()
    {
        var form = Form();
        form.Name = "dev";
        form.IngressReady = true;

        Assert.True(form.HasConfigPreview);
        Assert.Contains("ingress-ready=true", form.ConfigPreview!, StringComparison.Ordinal);
        Assert.DoesNotContain("ingress-nginx", form.ConfigPreview!, StringComparison.Ordinal);
    }
}
