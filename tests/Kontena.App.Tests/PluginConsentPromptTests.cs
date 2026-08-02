using System.Text.Json;
using Kontena.App;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Plugins;

namespace Kontena.App.Tests;

/// <summary>
/// A plugin found but not agreed to has to be asked about, and the answer has to survive a restart.
/// The prompt is the shell's ordinary confirm — what the manifest says is rendered, not composed.
/// </summary>
// Same collection as PluginCatalogTests — see BackendCatalogPluginState's own comment. Both classes
// mutate BackendCatalog.Plugins and reset it in Dispose; left in separate (default) collections, xunit's
// cross-class parallelism lets one class's Dispose clear the list mid-assertion in the other.
[Collection(BackendCatalogPluginState.Name)]
public sealed class PluginConsentPromptTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), "kontena-consent-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        BackendCatalog.ResetPluginProviders();
        if (File.Exists(_settingsPath))
            File.Delete(_settingsPath);
    }

    private static DiscoveredPlugin Awaiting(
        string id = "com.acme.nerdctl", string version = "1.0.0", string name = "nerdctl") =>
        new(
            Directory: Path.Combine(Path.GetTempPath(), id),
            Manifest: new PluginManifest
            {
                Id = id,
                Name = name,
                Version = version,
                Assembly = "Kontena.Plugins.Nerdctl.dll",
                Author = "Acme",
                Description = "containerd containers.",
            },
            Status: PluginStatus.AwaitingConsent,
            Reason: null,
            Providers: []);

    private MainWindowViewModel Build(params DiscoveredPlugin[] plugins) =>
        new(
            new BackendRegistry([]), new SettingsStore(_settingsPath), new KontenaSettings(),
            buildCatalog: (_, _, _, _) => [],
            plugins: plugins);

    /// <summary>Like <see cref="Build(DiscoveredPlugin[])"/>, but with the loader pointed at a real,
    /// throwaway directory instead of <see cref="PluginLoader.DefaultRoot"/> — so
    /// <c>AskPluginConsent</c>'s <c>OnConfirm</c> re-scan sees actual files rather than whatever
    /// happens (or does not happen) to be on this machine's real plugins folder.</summary>
    private MainWindowViewModel Build(string pluginRoot, params DiscoveredPlugin[] plugins) =>
        new(
            new BackendRegistry([]), new SettingsStore(_settingsPath), new KontenaSettings(),
            buildCatalog: (_, _, _, _) => [],
            plugins: plugins,
            pluginRoot: pluginRoot);

    /// <summary>Write a real plugin directory — manifest only, no assembly — under <paramref
    /// name="root"/>, for tests that exercise a real <see cref="PluginLoader.Discover"/> re-scan rather
    /// than a hand-built <see cref="DiscoveredPlugin"/>. No assembly is needed: whether it loads or is
    /// rejected for a missing file, either way it is no longer <see cref="PluginStatus.AwaitingConsent"/>
    /// once consent has been given, which is all these tests need.</summary>
    private static void WritePluginManifest(string root, string id, string name, string version = "1.0.0")
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id,
            name,
            version,
            author = "Acme",
            description = "containerd containers.",
            minSdkVersion = "",
            assembly = "Missing.dll",
        }));
    }

    [Fact]
    public void A_plugin_awaiting_consent_raises_a_confirm()
    {
        var vm = Build(Awaiting());

        vm.AskPluginConsent();

        var dialog = Assert.IsType<ConfirmViewModel>(vm.Dialog);
        Assert.Contains("nerdctl", dialog.Message);
    }

    [Fact]
    public void The_confirm_shows_what_the_manifest_says()
    {
        var vm = Build(Awaiting());

        vm.AskPluginConsent();

        var dialog = Assert.IsType<ConfirmViewModel>(vm.Dialog);
        Assert.Contains(dialog.Details, d => d.Detail.Contains("Acme"));
        Assert.Contains(dialog.Details, d => d.Detail.Contains("1.0.0"));
    }

    [Fact]
    public void The_confirm_is_not_styled_as_destructive()
    {
        // Nothing is being deleted — the question is whether to trust, and the danger styling would
        // say the wrong thing about it.
        var vm = Build(Awaiting());

        vm.AskPluginConsent();

        Assert.False(Assert.IsType<ConfirmViewModel>(vm.Dialog).Destructive);
    }

    [Fact]
    public void Nothing_is_asked_when_there_is_nothing_awaiting_consent()
    {
        var vm = Build();

        vm.AskPluginConsent();

        Assert.Null(vm.Dialog);
    }

    [Fact]
    public void A_rejected_plugin_is_not_asked_about()
    {
        var vm = Build(Awaiting() with { Status = PluginStatus.Rejected, Reason = "Needs a newer SDK" });

        vm.AskPluginConsent();

        Assert.Null(vm.Dialog);
    }

    [Fact]
    public async Task Confirming_records_consent_for_that_exact_version()
    {
        var vm = Build(Awaiting(version: "1.0.0"));
        vm.AskPluginConsent();

        await ((ConfirmViewModel)vm.Dialog!).ConfirmCommand.ExecuteAsync(null);

        var stored = new SettingsStore(_settingsPath).Load();
        Assert.True(stored.AllowsPlugin("com.acme.nerdctl", "1.0.0"));
        Assert.False(stored.AllowsPlugin("com.acme.nerdctl", "1.1.0"));
    }

    [Fact]
    public void Declining_records_nothing()
    {
        var vm = Build(Awaiting());
        vm.AskPluginConsent();

        ((ConfirmViewModel)vm.Dialog!).CancelCommand.Execute(null);

        Assert.Empty(new SettingsStore(_settingsPath).Load().AllowedPlugins);
        Assert.Null(vm.Dialog);
    }

    [Fact]
    public async Task An_approved_plugin_is_not_asked_about_again()
    {
        // InitAsync is not startup-only — ReconnectAsync runs it again without a restart — so a second
        // AskPluginConsent() in the same process must see this plugin as already answered: neither the
        // modal returning, nor another PluginLoadContext (each Discover() call creates one that is never
        // collectible) for a plugin the user already approved.
        //
        // A real pluginRoot with two directories on disk, not PluginLoader.DefaultRoot, is what makes
        // this test mean something (KON-279 final review, finding 2): on a machine with no plugins
        // folder — the normal case, including CI — OnConfirm's re-scan would come back empty and
        // Assert.Null(vm.Dialog) would pass whether or not the settings check in the pending filter
        // does anything at all. Asserting that the *second, still-pending* plugin is what comes up next
        // is what actually exercises that check, because an empty snapshot has no second plugin to ask
        // about either.
        var root = Path.Combine(Path.GetTempPath(), "kontena-consent-root-" + Guid.NewGuid().ToString("N"));
        WritePluginManifest(root, "com.acme.one", "first");
        WritePluginManifest(root, "com.acme.two", "second");

        try
        {
            var vm = Build(root, Awaiting("com.acme.one", name: "first"), Awaiting("com.acme.two", name: "second"));
            vm.AskPluginConsent();
            Assert.Contains("first", ((ConfirmViewModel)vm.Dialog!).Message);

            await ((ConfirmViewModel)vm.Dialog!).ConfirmCommand.ExecuteAsync(null);

            vm.AskPluginConsent();

            var dialog = Assert.IsType<ConfirmViewModel>(vm.Dialog);
            Assert.Contains("second", dialog.Message);
            Assert.DoesNotContain("first", dialog.Message);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Only_one_plugin_is_asked_about_at_a_time()
    {
        // Distinguishable names: a loop that raised a confirm per pending plugin would still leave a
        // ConfirmViewModel in Dialog (ShowConfirm overwrites unconditionally), so asserting the type
        // alone cannot tell that apart from asking about only the first. Naming the one actually asked
        // about can.
        var vm = Build(Awaiting("com.a.one", name: "first"), Awaiting("com.b.two", name: "second"));

        vm.AskPluginConsent();

        var dialog = Assert.IsType<ConfirmViewModel>(vm.Dialog);
        Assert.Contains("first", dialog.Message);
        Assert.DoesNotContain("second", dialog.Message);
    }
}
