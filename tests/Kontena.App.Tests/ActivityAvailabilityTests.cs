using Kontena.App.ViewModels;
using Kontena.Core.Orchestration.Fakes;

namespace Kontena.App.Tests;

/// <summary>
/// Activity is offered only where it has something to replay (KON-386).
/// <para>
/// The log attaches to the container engine's event stream and nothing attaches it in cluster mode, so
/// on a cluster the entry opened a page that stayed empty for the whole session — a dead button with a
/// page behind it (KON-117). A cluster answers the same question on its own System → Events page.
/// </para>
/// </summary>
public sealed class ActivityAvailabilityTests
{
    [Fact]
    public async Task Cluster_mode_drops_the_Activity_entry()
    {
        var shell = new MainWindowViewModel();
        Assert.True(shell.HasActivity);

        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        Assert.False(shell.HasActivity);
        // The About page offers the same jump, and a second door to the empty page is the same bug.
        Assert.False(shell.About.HasActivity);
    }

    [Fact]
    public async Task Leaving_cluster_mode_brings_it_back()
    {
        var shell = new MainWindowViewModel();
        Assert.True(await shell.EnterClusterModeAsync(new FakeClusterEngine()));

        shell.IsClusterMode = false;

        Assert.True(shell.HasActivity);
        Assert.True(shell.About.HasActivity);
    }
}
