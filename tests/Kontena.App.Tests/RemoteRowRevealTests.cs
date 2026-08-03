using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Engines;
using Kontena.Engines.Fakes;
using Kontena.Sdk;
using Kontena.Sdk.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The view-model half of "Manage below" (KON-264): which detected rows offer it, and what pressing
/// one asks for. Where it lands on screen is the view's job and is covered headlessly.
/// </summary>
public sealed class RemoteRowRevealTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-reveal-vm-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static readonly RemoteEngine Remote =
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", 22, "deploy");

    /// <summary>Stands in for the remote's provider — it is the backend id that ties the two rows together.</summary>
    private sealed class TestProvider(string backend) : IBackendProvider
    {
        public string Backend => backend;
        public string DisplayName => backend;
        public string Chip => "R";
        public BackendKind Kind => BackendKind.Engine;
        public IBackend CreateBackend() => new FakeEngine();
    }

    private SettingsViewModel Page(IEnumerable<EngineListItem> engines)
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true, RemoteEngines = [Remote] };
        store.Save(settings);

        return new SettingsViewModel(
            store, settings, [.. engines],
            new SettingsContext
            {
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            });
    }

    [Fact]
    public void Revealing_a_remote_asks_for_its_own_row()
    {
        var page = Page([new EngineListItem(
            Remote.Backend, "Build server", new BackendChipInfo("R"), "", false, false, IsRemote: true)]);

        RemoteEngineRow? asked = null;
        page.RevealRemoteRequested += (_, row) => asked = row;

        page.RevealRemoteCommand.Execute(Remote.Backend);

        Assert.NotNull(asked);
        Assert.Equal(Remote.Id, asked.Remote.Id);
    }

    [Fact]
    public void Revealing_a_backend_with_no_row_asks_for_nothing()
    {
        // A remote removed between the list being built and the click. Nothing was requested that can
        // fail — the row it pointed at is simply not there any more.
        var page = Page([]);

        var asked = 0;
        page.RevealRemoteRequested += (_, _) => asked++;

        page.RevealRemoteCommand.Execute("docker-remote:gone");
        page.RevealRemoteCommand.Execute(null);
        page.RevealRemoteCommand.Execute(string.Empty);

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Only_a_configured_remote_is_marked_as_having_a_row_below()
    {
        // Built by the shell, not by hand: whether a detected engine is one of the user's remotes is
        // decided there, against settings, and that is the decision this asserts.
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings { Onboarded = true, RemoteEngines = [Remote] };
        store.Save(settings);

        var shell = new MainWindowViewModel(
            new BackendRegistry([new TestProvider("docker"), new TestProvider(Remote.Backend)]),
            store, settings, new FakeUpdateService());

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (shell.SettingsPage is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.NotNull(shell.SettingsPage);

        var rows = shell.SettingsPage.Engines;

        Assert.True(rows.Single(e => e.Backend == Remote.Backend).IsRemote);
        // Docker's actions are nowhere else, because it has none: an inventory is not where you
        // remove an engine from.
        Assert.False(rows.Single(e => e.Backend == "docker").IsRemote);
    }
}
