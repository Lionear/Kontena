using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Choosing how often the Alerts page re-reads (KON-393). One picker with Off as its first entry,
/// rather than a switch and a number that can disagree about whether polling is on.
/// </summary>
public sealed class AlertRefreshSettingTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-alert-refresh-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SettingsViewModel Page(KontenaSettings? settings = null)
    {
        var store = new SettingsStore(_path);
        var loaded = settings ?? new KontenaSettings();
        store.Save(loaded);
        return new SettingsViewModel(store, loaded, []);
    }

    private KontenaSettings OnDisk() => new SettingsStore(_path).Load();

    [Fact]
    public void The_picker_opens_on_what_is_stored()
    {
        var page = Page(new KontenaSettings { AlertRefreshSeconds = 300 });

        Assert.Equal("Every 5 minutes", page.AlertRefreshChoice);
        Assert.Equal(AlertRefresh.Choices.Count, page.AlertRefreshOptions.Count);
        Assert.Equal("Off", page.AlertRefreshOptions[0]);
    }

    [Fact]
    public void Choosing_an_interval_is_written_straight_away()
    {
        var page = Page();

        page.AlertRefreshChoice = "Every 5 minutes";
        Assert.Equal(300, OnDisk().AlertRefreshSeconds);

        // Off is a choice in the same list, and it persists like any other.
        page.AlertRefreshChoice = "Off";
        Assert.Equal(0, OnDisk().AlertRefreshSeconds);

        // The hint under the picker follows, because what off means is not obvious from "Off".
        Assert.Contains("when you open it", page.AlertRefreshHint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_edited_interval_joins_the_picker_rather_than_being_snapped_to_a_neighbour()
    {
        // The settings file is JSON somebody can open. Showing 45 seconds as "Every 30 seconds"
        // would have this page describe an Alerts page that is doing something else.
        var page = Page(new KontenaSettings { AlertRefreshSeconds = 45 });

        Assert.Equal("Every 45 seconds", page.AlertRefreshChoice);
        Assert.Contains("Every 45 seconds", page.AlertRefreshOptions);
        Assert.Equal(AlertRefresh.Choices.Count + 1, page.AlertRefreshOptions.Count);

        // And it survives an unrelated change to the page.
        page.CompactDensity = true;
        Assert.Equal(45, OnDisk().AlertRefreshSeconds);
    }
}
