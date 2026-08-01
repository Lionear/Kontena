using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk.Models;
using Xunit;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Changing a stored remote engine instead of removing and retyping it (KON-125).
/// <para>
/// The assertions are all about the id. It is what the name the user gave, the keychain entry, the
/// remembered port forwards and a launch pin are keyed by, so an "edit" that quietly minted a new one
/// would take all four with it — and none of that is visible at the moment of clicking Save.
/// </para>
/// </summary>
public sealed class EditRemoteEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-remote-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static readonly RemoteEngine Existing =
        new("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", User: "deploy");

    private static readonly RemoteEngine Other =
        new("r2", "Lab", RemoteEngineTransport.Ssh, "lab-01");

    private SettingsViewModel Subject(out SettingsStore store)
    {
        store = new SettingsStore(_path);
        var settings = new KontenaSettings { RemoteEngines = [Existing, Other] };
        store.Save(settings);

        return new SettingsViewModel(
            store, settings, [],
            new SettingsContext
            {
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            });
    }

    [Fact]
    public void Editing_loads_the_stored_remote_into_the_form()
    {
        var vm = Subject(out _);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);

        Assert.True(vm.IsEditingRemote);
        Assert.Equal("build-01", vm.RemoteHost);
        Assert.Equal("deploy", vm.RemoteUser);
        Assert.Equal("Build server", vm.RemoteName);
        Assert.True(vm.RemoteIsSsh);

        // The button says what it will do. "Add engine" over a form holding an existing remote is a
        // promise to create a second one.
        Assert.Equal("Save changes", vm.RemoteSubmitLabel);
    }

    [Fact]
    public async Task Saving_an_edit_keeps_the_id()
    {
        var vm = Subject(out var store);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);
        vm.RemoteHost = "build-02";
        await vm.AddRemoteCommand.ExecuteAsync(null);

        var stored = store.Load().RemoteEngines;

        Assert.Equal(2, stored.Count);
        Assert.Equal("r1", stored[0].Id);
        Assert.Equal("build-02", stored[0].Host);
        Assert.Equal("deploy", stored[0].User);
    }

    [Fact]
    public async Task Saving_an_edit_leaves_the_remote_where_it_was()
    {
        // The switcher reads this order. An edit that moved the entry to the bottom would read as
        // something else having changed.
        var vm = Subject(out var store);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);
        vm.RemoteName = "Build server 2";
        await vm.AddRemoteCommand.ExecuteAsync(null);

        var stored = store.Load().RemoteEngines;
        Assert.Equal(["r1", "r2"], stored.Select(r => r.Id));
        Assert.Equal("Build server 2", stored[0].Name);
    }

    [Fact]
    public async Task The_form_goes_back_to_adding_after_a_save()
    {
        var vm = Subject(out var store);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);
        vm.RemoteHost = "build-02";
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditingRemote);
        Assert.Empty(vm.RemoteHost);
        Assert.Equal("Add engine", vm.RemoteSubmitLabel);

        // And the next submit really does add, rather than editing the same one again.
        vm.RemoteHost = "build-03";
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.Equal(3, store.Load().RemoteEngines.Count);
    }

    [Fact]
    public async Task Cancelling_writes_nothing()
    {
        var vm = Subject(out var store);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);
        vm.RemoteHost = "typo";
        vm.CancelEditRemoteCommand.Execute(null);

        Assert.False(vm.IsEditingRemote);
        Assert.Empty(vm.RemoteHost);
        Assert.Equal("build-01", store.Load().RemoteEngines[0].Host);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_edit_that_would_be_refused_is_not_written()
    {
        // The TCP rule applies to an edit exactly as it does to a new one: turning a working SSH remote
        // into an unauthenticated TCP endpoint is the same decision either way.
        var vm = Subject(out var store);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);
        vm.SetRemoteTransportCommand.Execute("tcp");
        await vm.AddRemoteCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RemoteError);
        Assert.True(vm.IsEditingRemote);

        var stored = store.Load().RemoteEngines[0];
        Assert.Equal(RemoteEngineTransport.Ssh, stored.Transport);
        Assert.Equal("build-01", stored.Host);
    }

    [Fact]
    public async Task Removing_the_remote_being_edited_leaves_edit_mode()
    {
        // Otherwise the next Save writes back a remote that was just deleted.
        var vm = Subject(out var store);

        vm.EditRemoteCommand.Execute(vm.RemoteEngines[0]);

        // Removing asks first now (KON-126), so the test confirms on the user's behalf.
        ConfirmRequest? asked = null;
        vm.RequestConfirm = request => asked = request;
        vm.RemoveRemoteCommand.Execute(vm.RemoteEngines[0]);
        Assert.NotNull(asked);
        await asked.OnConfirm();

        Assert.False(vm.IsEditingRemote);
        Assert.Empty(vm.RemoteHost);
        Assert.Single(store.Load().RemoteEngines);
    }
}
