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

    private static DiscoveredPlugin Awaiting(string id = "com.acme.nerdctl", string version = "1.0.0") =>
        new(
            Directory: Path.Combine(Path.GetTempPath(), id),
            Manifest: new PluginManifest
            {
                Id = id,
                Name = "nerdctl",
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
    public void Only_one_plugin_is_asked_about_at_a_time()
    {
        var vm = Build(Awaiting("com.a.one"), Awaiting("com.b.two"));

        vm.AskPluginConsent();

        Assert.IsType<ConfirmViewModel>(vm.Dialog);
    }
}
