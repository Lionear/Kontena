using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.Tests;

/// <summary>
/// Shortcuts you can change (KON-180). KON-172 put five of them in XAML, where the mapping existed
/// only as a binding name — so it could not be shown, changed or checked.
/// </summary>
public sealed class ConfigurableShortcutTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-shortcuts-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SettingsViewModel Page(KontenaSettings? settings = null, Action? onChanged = null)
    {
        var store = new SettingsStore(_path);
        var loaded = settings ?? new KontenaSettings();
        store.Save(loaded);

        return new SettingsViewModel(store, loaded, [])
        {
            RequestShortcutsChanged = onChanged,
        };
    }

    private static ShortcutRow Row(SettingsViewModel page, string id) =>
        page.Shortcuts.Single(r => r.Action.Id == id);

    private KontenaSettings OnDisk() => new SettingsStore(_path).Load();

    [Fact]
    public void Every_action_starts_on_its_default()
    {
        var page = Page();

        Assert.Equal(ShellActions.All.Count, page.Shortcuts.Count);
        Assert.All(page.Shortcuts, r => Assert.True(r.IsDefault));
        Assert.False(page.HasCustomShortcuts);
    }

    [Fact]
    public void One_platform_gets_one_default()
    {
        // KON-172 registered Ctrl and Cmd side by side, so Ctrl+F also worked on macOS where it is not
        // the convention. The default is the platform's, and only the platform's.
        var search = ShellActions.All.Single(a => a.Id == ShellActions.FocusSearch);

        Assert.Equal(OperatingSystem.IsMacOS() ? "Cmd+F" : "Ctrl+F", search.DefaultGesture);
    }

    [Fact]
    public void Changing_one_stores_only_that_one()
    {
        var page = Page();

        Assert.True(Row(page, ShellActions.RefreshPage).Offer("Ctrl+Shift+R"));

        var stored = OnDisk().Shortcuts;
        Assert.Equal(new[] { ShellActions.RefreshPage }, stored.Keys);
        Assert.Equal("Ctrl+Shift+R", stored[ShellActions.RefreshPage]);
    }

    [Fact]
    public void A_stored_shortcut_is_what_the_next_launch_uses()
    {
        var page = Page();
        Row(page, ShellActions.GoBack).Offer("Ctrl+Shift+B");

        var next = Page(OnDisk());

        Assert.Equal("Ctrl+Shift+B", Row(next, ShellActions.GoBack).Gesture);
        Assert.False(Row(next, ShellActions.GoBack).IsDefault);
        Assert.True(next.HasCustomShortcuts);
    }

    [Fact]
    public void Setting_a_shortcut_back_to_its_default_stores_nothing()
    {
        // The absent key is the point: a default improved in a later release has to reach the people
        // who never changed it, and a stored copy of today's value would stop that.
        var page = Page();
        var row = Row(page, ShellActions.RefreshPage);

        row.Offer("Ctrl+Shift+R");
        row.Offer(row.Action.DefaultGesture);

        Assert.Empty(OnDisk().Shortcuts);
        Assert.True(row.IsDefault);
        Assert.False(page.HasCustomShortcuts);
    }

    [Fact]
    public void A_gesture_another_action_already_has_is_refused_by_name()
    {
        // Refused rather than resolved: letting the last one win leaves a shortcut that used to work
        // and now does nothing, with nothing on screen saying why.
        var page = Page();
        var refresh = Row(page, ShellActions.RefreshPage);

        var accepted = refresh.Offer(Row(page, ShellActions.FocusSearch).Gesture);

        Assert.False(accepted);
        Assert.Contains("Focus search", refresh.Problem, StringComparison.Ordinal);
        Assert.True(refresh.IsDefault);
        Assert.Empty(OnDisk().Shortcuts);
    }

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+D")]
    [InlineData("Ctrl+Z")]
    public void The_keys_that_control_a_running_process_stay_with_the_terminal(string gesture)
    {
        var page = Page();
        var row = Row(page, ShellActions.RefreshPage);

        Assert.False(row.Offer(gesture));
        Assert.Contains("terminal", row.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_editing_key_is_not_reserved()
    {
        // Ctrl+R is the shipped default for Refresh and also reverse-search in a shell. Both are true:
        // bindings live on the window, so a focused terminal answers first and Kontena never sees it.
        var page = Page();

        Assert.True(Row(page, ShellActions.FocusSearch).Offer("Ctrl+L"));
    }

    [Fact]
    public void Nonsense_is_refused_rather_than_thrown()
    {
        var page = Page();
        var row = Row(page, ShellActions.GoBack);

        Assert.False(row.Offer("Ctrl+"));
        Assert.NotEmpty(row.Problem);
        Assert.True(row.IsDefault);
    }

    [Fact]
    public void Restoring_one_default_is_refused_while_another_action_holds_it()
    {
        // Reachable: move Back out of the way, give its keys to Refresh, then ask for Back's default
        // back. Granting it silently would leave two bindings on one gesture and both would fire.
        var page = Page();
        var back = Row(page, ShellActions.GoBack);
        var backDefault = back.Action.DefaultGesture;

        back.Offer("Ctrl+Shift+B");
        Assert.True(Row(page, ShellActions.RefreshPage).Offer(backDefault));

        back.ResetCommand.Execute(null);

        Assert.Contains("Refresh page", back.Problem, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Shift+B", back.Gesture);
    }

    [Fact]
    public void Restoring_everything_always_works()
    {
        // The escape hatch that makes a per-row refusal safe to live with.
        var page = Page();
        Row(page, ShellActions.GoBack).Offer("Ctrl+Shift+B");
        Row(page, ShellActions.RefreshPage).Offer(Row(page, ShellActions.GoBack).Action.DefaultGesture);

        page.ResetAllShortcutsCommand.Execute(null);

        Assert.All(page.Shortcuts, r => Assert.True(r.IsDefault));
        Assert.Empty(OnDisk().Shortcuts);
        Assert.False(page.HasCustomShortcuts);
    }

    [Fact]
    public void Changing_one_asks_the_shell_to_rebind()
    {
        // Without this the change would only take effect on the next launch.
        var asked = 0;
        var page = Page(onChanged: () => asked++);

        Row(page, ShellActions.RefreshPage).Offer("Ctrl+Shift+R");

        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_refused_gesture_leaves_the_row_listening()
    {
        var page = Page();
        var row = Row(page, ShellActions.RefreshPage);
        row.RecordCommand.Execute(null);

        row.Offer("Ctrl+C");
        Assert.True(row.IsRecording);

        row.Offer("Ctrl+Shift+R");
        Assert.False(row.IsRecording);
    }

    [Fact]
    public void Every_action_in_the_registry_has_a_command_behind_it()
    {
        // A row in Settings that binds to nothing is a shortcut that cannot work.
        var store = new SettingsStore(_path);
        using var shell = new MainWindowViewModel(
            new BackendRegistry([]), store, store.Load(), new FakeUpdateService());

        Assert.All(ShellActions.All, a => Assert.True(shell.ShortcutCommands.ContainsKey(a.Id)));
    }

    [Fact]
    public void The_shortcut_is_named_on_the_button_that_does_the_same_thing()
    {
        // A shortcut you cannot find is one you do not have.
        var store = new SettingsStore(_path);
        store.Save(new KontenaSettings
        {
            Shortcuts = new Dictionary<string, string> { [ShellActions.RefreshPage] = "Ctrl+Shift+R" },
        });

        using var shell = new MainWindowViewModel(
            new BackendRegistry([]), store, store.Load(), new FakeUpdateService());

        Assert.Equal("Refresh (Ctrl+Shift+R)", shell.RefreshTooltip);
    }
}
