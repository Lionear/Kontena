using Kontena.App;
using Kontena.App.ViewModels;
using Kontena.Engines.Fakes;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The Run dialog stops offering a restart policy on a backend that has none.
/// <para>
/// Apple's <c>container</c> is that backend: its CLI has no restart flag of any kind, so a policy
/// chosen here would be accepted by the form and then never happen — leaving someone believing their
/// container comes back after a crash. Same class of fault as a button that does nothing, and Rick
/// found this one by looking at the dialog (KON-31).
/// </para>
/// </summary>
public sealed class RunContainerRestartPolicyTests
{
    private static RunContainerViewModel Dialog(bool supportsRestartPolicy)
    {
        var engine = new FakeEngine(seed: false)
        {
            Capabilities = new EngineCapabilities { SupportsRestartPolicy = supportsRestartPolicy },
        };

        return new RunContainerViewModel(
            engine,
            backendName: "test",
            backendChip: new BackendChipInfo("T"),
            networks: [],
            localImages: new HashSet<string>(),
            onClose: () => { },
            onCreated: () => Task.CompletedTask);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_field_is_offered_only_where_the_engine_can_honour_it(bool supported)
    {
        Assert.Equal(supported, Dialog(supported).SupportsRestartPolicy);
    }

    /// <summary>
    /// With the field gone, the container-name box takes the whole row instead of staying half-width
    /// beside the gap where a control used to be.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void The_name_field_widens_where_the_restart_field_is_hidden(bool supported, int expectedSpan)
    {
        Assert.Equal(expectedSpan, Dialog(supported).NameColumnSpan);
    }

    /// <summary>
    /// The selection stays at "no" on such a backend, so the command preview cannot advertise a flag the
    /// engine does not have — this is what makes hiding the field sufficient rather than only tidy.
    /// </summary>
    [Fact]
    public void The_preview_never_shows_a_restart_flag_without_the_field()
    {
        var dialog = Dialog(supportsRestartPolicy: false);
        dialog.Image = "alpine:3.20";

        Assert.Equal("no", dialog.SelectedRestartPolicy);
        Assert.DoesNotContain("--restart", dialog.CommandPreview, StringComparison.Ordinal);
    }
}
