using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;

namespace Kontena.App.Tests;

/// <summary>
/// Starting on a backend whose adapter the user switched off says so, rather than blaming a removed
/// kube-context or an uninstalled engine (KON-283).
/// <para>
/// The path already existed for a backend that went away on its own. What changed is that one of the
/// reasons is now the user's own doing and is undone in one click — and sending someone to look at
/// their machine for something they did in Settings is the wrong place to send them.
/// </para>
/// </summary>
public sealed class SwitchedOffAdapterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-switched-off-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    /// <summary>Stands in for whatever is left after the switched-off adapter stopped contributing.</summary>
    private sealed class TestEngineProvider(string backend) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => backend[..1].ToUpperInvariant();
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new FakeEngine();
    }

    private async Task<MainWindowViewModel> ShellAsync(KontenaSettings settings)
    {
        var store = new SettingsStore(_path);
        store.Save(settings);

        // Only the engine that is still on. The switched-off one is absent exactly as BackendCatalog
        // would leave it — which is the situation the message has to explain.
        var vm = new MainWindowViewModel(
            new BackendRegistry([new TestEngineProvider("docker")]), store, settings,
            new FakeUpdateService());

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.IsReady && !vm.IsBackendDown && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(vm.IsReady || vm.IsBackendDown, "the shell never finished starting");
        return vm;
    }

    [Fact]
    public async Task The_message_names_the_adapter_the_user_switched_off()
    {
        var vm = await ShellAsync(new KontenaSettings
        {
            Onboarded = true,
            PinnedBackend = "podman",
            Startup = StartupBackend.Pinned,
            DisabledAdapters = ["podman"],
        });

        Assert.True(vm.IsBackendDown);
        Assert.Contains("Podman", vm.BackendDownDetail, StringComparison.Ordinal);
        Assert.Contains("Settings", vm.BackendDownDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The old wording sent people looking at their machine. It still has to, for the cases where that
    /// is where the answer is.
    /// </summary>
    [Fact]
    public async Task A_backend_that_went_away_on_its_own_still_blames_the_machine()
    {
        var vm = await ShellAsync(new KontenaSettings
        {
            Onboarded = true,
            PinnedBackend = "kubernetes:prod-eu-west",
            Startup = StartupBackend.Pinned,
        });

        Assert.True(vm.IsBackendDown);
        Assert.Contains("no longer available", vm.BackendDownDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings › Extensions", vm.BackendDownDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A context out of a switched-off Kubernetes adapter is the adapter's doing, not the kubeconfig's —
    /// the id carries the context, and the adapter behind it is what the user turned off.
    /// </summary>
    [Fact]
    public async Task A_kube_context_from_a_switched_off_adapter_names_the_adapter()
    {
        var vm = await ShellAsync(new KontenaSettings
        {
            Onboarded = true,
            PinnedBackend = "kubernetes:prod-eu-west",
            Startup = StartupBackend.Pinned,
            DisabledAdapters = ["kubernetes"],
        });

        Assert.True(vm.IsBackendDown);
        Assert.Contains("Kubernetes", vm.BackendDownDetail, StringComparison.Ordinal);
    }
}
