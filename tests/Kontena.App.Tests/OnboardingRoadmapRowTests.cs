using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// The "Apple container · Coming soon" row is a full-size engine row on the first screen of the app,
/// and on Linux or Windows it announced a runtime those platforms will never get (KON-337).
/// </summary>
public sealed class OnboardingRoadmapRowTests
{
    private static OnboardingViewModel Wizard(bool showRoadmap) => new(
        probes: [],
        fakeBackend: "fake",
        autoDetect: true,
        onContinue: _ => { },
        onSkip: () => { },
        onInstallPodman: () => { },
        onRescan: () => Task.CompletedTask,
        onStartEngine: () => Task.CompletedTask,
        showRoadmap: showRoadmap);

    [Fact]
    public void Roadmap_row_is_absent_where_it_can_never_apply()
    {
        Assert.Empty(Wizard(showRoadmap: false).Engines);
    }

    [Fact]
    public void Roadmap_row_is_present_on_macOS()
    {
        var row = Assert.Single(Wizard(showRoadmap: true).Engines);

        Assert.Equal("apple", row.Backend);
        Assert.True(row.ComingSoon);
        Assert.False(row.Selectable);
    }
}
