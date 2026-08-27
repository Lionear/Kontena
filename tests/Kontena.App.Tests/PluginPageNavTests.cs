using Avalonia.Controls;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Plugins;
using Kontena.Sdk;

namespace Kontena.App.Tests;

/// <summary>
/// The UI-contribution seam from the shell's side (KON-331): a plugin that contributes pages gets
/// sidebar entries, every one of them says it is a plugin, and a page that cannot be built costs its
/// own content area rather than the window.
/// </summary>
public sealed class PluginPageNavTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), "kontena-plugin-nav-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_settingsPath))
            File.Delete(_settingsPath);
    }

    private static DiscoveredPlugin Loaded(params PluginPage[] pages) =>
        new(
            Directory: Path.Combine(Path.GetTempPath(), "com.acme.studio"),
            Manifest: new PluginManifest
            {
                Id = "com.acme.studio",
                Name = "Studio",
                Version = "1.0.0",
                Assembly = "Acme.Studio.dll",
            },
            Status: PluginStatus.Loaded,
            Reason: null,
            Providers: [])
        {
            Pages = pages,
        };

    private static PluginPage Page(string key, string label = "Editor") =>
        new(key, label, "IconBox", _ => new TextBlock { Text = label });

    private MainWindowViewModel Build(params DiscoveredPlugin[] plugins) =>
        new(
            new BackendRegistry([]), new SettingsStore(_settingsPath), new KontenaSettings(),
            buildCatalog: (_, _, _, _, _) => [],
            plugins: plugins);

    private static NavItem[] PluginItems(MainWindowViewModel vm) =>
        [.. vm.NavGroups.SelectMany(g => g.Items).Where(i => i.IsPlugin)];

    [Fact]
    public void A_page_a_plugin_contributes_gets_a_sidebar_entry()
    {
        using var vm = Build(Loaded(Page("editor")));

        var item = Assert.Single(PluginItems(vm));

        Assert.Equal("Editor", item.Label);
        // Prefixed with the plugin id, so two plugins naming a page "editor" stay two entries.
        Assert.Equal("plugin:com.acme.studio:editor", item.Key);
    }

    [Fact]
    public void A_plugin_that_contributes_nothing_adds_no_group()
    {
        using var vm = Build(Loaded());

        Assert.Empty(PluginItems(vm));
        Assert.DoesNotContain(vm.NavGroups, g => g.Label == "Plugins");
    }

    [Fact]
    public void A_plugin_awaiting_consent_contributes_no_entry()
    {
        // Status is the loader's answer about whether this build may run at all. An entry here would be
        // a page the user never agreed to, one click away.
        var pending = Loaded(Page("editor")) with { Status = PluginStatus.AwaitingConsent };

        using var vm = Build(pending);

        Assert.Empty(PluginItems(vm));
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("diff")]
    [InlineData("source")]
    public void Every_entry_from_a_plugin_carries_the_badge(string key)
    {
        // A theory over the entries rather than one assertion on the first: a second contribution shape
        // must not be able to skip the badge quietly.
        using var vm = Build(Loaded(Page("editor"), Page("diff", "Diff"), Page("source", "Source")));

        var item = Assert.Single(
            vm.NavGroups.SelectMany(g => g.Items),
            i => i.Key.EndsWith(':' + key, StringComparison.Ordinal));

        Assert.True(item.IsPlugin, $"The entry for '{key}' does not say it came from a plugin.");
        Assert.Contains("Studio", item.PluginTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_a_page_puts_the_plugins_own_control_on_screen()
    {
        using var vm = Build(Loaded(Page("editor")));

        vm.NavigateCommand.Execute("plugin:com.acme.studio:editor");

        var view = Assert.IsType<TextBlock>(vm.CurrentPage);
        Assert.Equal("Editor", view.Text);
        Assert.True(PluginItems(vm)[0].IsSelected);
    }

    [Fact]
    public void The_page_is_handed_the_host_when_it_is_built()
    {
        // The seam exists so a page can reach the cluster the user is in. Nothing is connected here, so
        // what this proves is the handing over — and that "no cluster" arrives as null rather than as a
        // missing argument the plugin has to guess about.
        IPluginHost? seen = null;
        var page = new PluginPage("editor", "Editor", "IconBox", host =>
        {
            seen = host;
            return new TextBlock();
        });

        using var vm = Build(Loaded(page));

        vm.NavigateCommand.Execute("plugin:com.acme.studio:editor");

        Assert.NotNull(seen);
        Assert.Null(seen.Cluster);
    }

    [Fact]
    public void A_page_that_throws_while_being_built_reports_and_leaves_the_shell_standing()
    {
        var exploding = new PluginPage(
            "editor", "Editor", "IconBox", _ => throw new InvalidOperationException("no editor for you"));

        using var vm = Build(Loaded(exploding));

        vm.NavigateCommand.Execute("plugin:com.acme.studio:editor");

        var shown = Assert.IsType<TextBlock>(vm.CurrentPage);
        Assert.Contains("no editor for you", shown.Text, StringComparison.Ordinal);
        // Still navigable: the failure took the page, not the window around it.
        Assert.NotEmpty(vm.NavGroups);
        Assert.True(PluginItems(vm)[0].IsSelected);
    }
}
