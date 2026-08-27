using Kontena.Adapters.Docker;
using Kontena.Adapters.Podman;
using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Settings › Extensions: the switch writes through, and switching off something that is open asks
/// first (KON-283).
/// </summary>
public sealed class ExtensionsSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-extensions-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static readonly EngineListItem[] Backends =
    [
        new("docker", "Docker", new BackendChipInfo("D"), "", Connected: true, IsDefault: false),
        new("podman", "Podman", new BackendChipInfo("P"), "", Connected: true, IsDefault: false),
    ];

    private SettingsViewModel Page(
        KontenaSettings? settings = null, string? active = null, Action<ConfirmRequest>? confirm = null,
        Func<Task>? changed = null)
    {
        var resolved = settings ?? new KontenaSettings();
        var store = new SettingsStore(_path);
        store.Save(resolved);

        return new SettingsViewModel(store, resolved, Backends, new SettingsContext
        {
            Backends = Backends,
            Adapters = AdapterCatalog.All([]),
            ActiveBackend = active,
            OnAdaptersChanged = changed,
            Autostart = new UnsupportedAutostart(),
            Secrets = new UnavailableSecretStore(),
        })
        {
            RequestConfirm = confirm,
        };
    }

    [Fact]
    public void The_bundled_adapters_are_listed_and_on()
    {
        var page = Page();

        Assert.True(page.HasAdapters);
        Assert.All(page.Adapters, row => Assert.True(row.IsEnabled));
        Assert.Contains(page.Adapters, row => row.Id == DockerAdapterModule.BackendId);
    }

    [Fact]
    public void A_row_reads_switched_off_when_the_settings_say_so()
    {
        var page = Page(new KontenaSettings { DisabledAdapters = ["podman"] });

        Assert.False(Assert.Single(page.Adapters, r => r.Id == PodmanAdapterModule.BackendId).IsEnabled);
    }

    [Fact]
    public async Task Switching_one_off_persists_and_rebuilds()
    {
        var rebuilt = 0;
        var page = Page(changed: () => { rebuilt++; return Task.CompletedTask; });

        Assert.Single(page.Adapters, r => r.Id == PodmanAdapterModule.BackendId).IsEnabled = false;

        // The write is synchronous; the rebuild it triggers is not.
        await WaitFor(() => rebuilt == 1);
        Assert.False(new SettingsStore(_path).Load().IsAdapterEnabled(PodmanAdapterModule.BackendId));
    }

    [Fact]
    public async Task Switching_one_back_on_persists_too()
    {
        var page = Page(new KontenaSettings { DisabledAdapters = ["podman"] });

        Assert.Single(page.Adapters, r => r.Id == PodmanAdapterModule.BackendId).IsEnabled = true;

        await WaitFor(() => new SettingsStore(_path).Load().IsAdapterEnabled(PodmanAdapterModule.BackendId));
    }

    /// <summary>
    /// Nothing is open on it, so there is nothing to warn about — a dialog that always appears is one
    /// nobody reads.
    /// </summary>
    [Fact]
    public void Switching_off_an_adapter_nothing_uses_asks_nothing()
    {
        ConfirmRequest? asked = null;
        var page = Page(confirm: r => asked = r);

        Assert.Single(page.Adapters, r => r.Id == PodmanAdapterModule.BackendId).IsEnabled = false;

        Assert.Null(asked);
    }

    [Fact]
    public void Switching_off_the_adapter_behind_the_open_backend_asks_first()
    {
        ConfirmRequest? asked = null;
        var page = Page(active: "docker", confirm: r => asked = r);
        var row = Assert.Single(page.Adapters, r => r.Id == DockerAdapterModule.BackendId);

        row.IsEnabled = false;

        Assert.NotNull(asked);
        Assert.Contains("Docker", asked.Title, StringComparison.Ordinal);
        Assert.Contains("Docker", asked.Details?.Single().Detail ?? string.Empty, StringComparison.Ordinal);

        // Declining leaves it on, and the switch has to follow — it moved before the question was asked.
        Assert.True(row.IsEnabled);
        Assert.True(new SettingsStore(_path).Load().IsAdapterEnabled(DockerAdapterModule.BackendId));
    }

    [Fact]
    public async Task Confirming_the_question_switches_it_off()
    {
        ConfirmRequest? asked = null;
        var page = Page(active: "docker", confirm: r => asked = r);
        var row = Assert.Single(page.Adapters, r => r.Id == DockerAdapterModule.BackendId);

        row.IsEnabled = false;
        await asked!.OnConfirm();

        Assert.False(row.IsEnabled);
        Assert.False(new SettingsStore(_path).Load().IsAdapterEnabled(DockerAdapterModule.BackendId));
    }

    /// <summary>The one the next launch would open counts as in use too — it breaks on restart.</summary>
    [Fact]
    public void Switching_off_the_adapter_behind_the_startup_target_asks_first()
    {
        ConfirmRequest? asked = null;
        var page = Page(
            new KontenaSettings { PinnedBackend = "podman", Startup = StartupBackend.Pinned },
            confirm: r => asked = r);

        Assert.Single(page.Adapters, r => r.Id == PodmanAdapterModule.BackendId).IsEnabled = false;

        Assert.NotNull(asked);
    }

    /// <summary>Turning one back on takes nothing away, so it never asks.</summary>
    [Fact]
    public void Switching_one_on_never_asks()
    {
        ConfirmRequest? asked = null;
        var page = Page(
            new KontenaSettings { DisabledAdapters = ["docker"] }, active: "docker", confirm: r => asked = r);

        Assert.Single(page.Adapters, r => r.Id == DockerAdapterModule.BackendId).IsEnabled = true;

        Assert.Null(asked);
    }

    private static async Task WaitFor(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(done(), "the change never arrived");
    }
}
