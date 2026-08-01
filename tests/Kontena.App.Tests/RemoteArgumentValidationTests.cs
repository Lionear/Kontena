using Kontena.App.Services;
using Kontena.App.ViewModels;
using Kontena.Sdk.Models;
using Xunit;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The remote-engine form against values ssh would read as its own options (KON-181).
/// <para>
/// The rule itself is tested next to the command line it protects, in
/// <c>SshTunnelTests</c>. These are about the form: that it refuses, that it says why, and that it
/// notices while you are typing the field in question.
/// </para>
/// </summary>
public sealed class RemoteArgumentValidationTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"kontena-remote-args-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SettingsViewModel Form()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings();
        store.Save(settings);

        var vm = new SettingsViewModel(
            store, settings, [],
            new SettingsContext
            {
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            });

        vm.RemoteName = "Build server";
        vm.RemoteHost = "build-01";
        return vm;
    }

    [Fact]
    public void A_plain_ssh_remote_is_accepted()
    {
        var vm = Form();

        Assert.Null(vm.RemoteProblem);
        Assert.True(vm.CanAddRemote);
    }

    [Fact]
    public void A_host_that_is_really_an_ssh_option_is_refused_and_says_so()
    {
        var vm = Form();

        vm.RemoteHost = "-oProxyCommand=touch /tmp/pwned";

        Assert.False(vm.CanAddRemote);
        Assert.NotNull(vm.RemoteProblem);
        Assert.Contains("option", vm.RemoteProblem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_is_judged_while_it_is_being_typed()
    {
        // RemoteUser had no change handler at all, so the submit button did not re-evaluate until some
        // other field moved. Invisible while the only rule was about certificates; wrong as soon as the
        // user carries a rule of its own.
        var vm = Form();

        vm.RemoteUser = "-oProxyCommand=id";

        Assert.False(vm.CanAddRemote);
        Assert.NotNull(vm.RemoteProblem);
    }

    [Fact]
    public void A_socket_path_is_judged_while_it_is_being_typed()
    {
        var vm = Form();

        vm.RemoteSocketPath = "evil.example:22";

        Assert.False(vm.CanAddRemote);
        Assert.NotNull(vm.RemoteProblem);
    }

    [Fact]
    public void Correcting_the_value_clears_the_complaint()
    {
        var vm = Form();
        vm.RemoteUser = "-x";
        Assert.False(vm.CanAddRemote);

        vm.RemoteUser = "deploy";

        Assert.Null(vm.RemoteProblem);
        Assert.True(vm.CanAddRemote);
    }

    [Fact]
    public void An_ordinary_hyphenated_host_is_left_alone()
    {
        // The rule is about the first character. Hyphens inside a name are the normal case — build-01,
        // and ssh_config aliases like my-jump-host.
        var vm = Form();

        vm.RemoteHost = "my-jump-host";
        vm.RemoteSocketPath = "/run/user/1000/podman/podman.sock";

        Assert.Null(vm.RemoteProblem);
        Assert.True(vm.CanAddRemote);
    }

    [Fact]
    public void A_remote_that_never_came_from_a_form_explains_itself_in_the_list()
    {
        // The vector from the ticket: a settings file edited by hand, synced from another machine, or
        // written by an older version. The switcher skips such a remote rather than offering an entry
        // that cannot connect — so this list is the only place left that can say why, and "not
        // reachable" would send someone looking at their network.
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings
        {
            RemoteEngines =
            [
                new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh, "-oProxyCommand=id"),
            ],
        };
        store.Save(settings);

        var vm = new SettingsViewModel(
            store, settings, [],
            new SettingsContext
            {
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            });

        var row = Assert.Single(vm.RemoteEngines);
        Assert.True(row.HasProblem);
        Assert.Contains("option", row.Problem!, StringComparison.Ordinal);
        Assert.NotEqual("not reachable", row.Status);
    }

    [Fact]
    public void A_healthy_remote_says_nothing_extra()
    {
        var store = new SettingsStore(_path);
        var settings = new KontenaSettings
        {
            RemoteEngines =
            [
                new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", User: "deploy"),
            ],
        };
        store.Save(settings);

        var vm = new SettingsViewModel(
            store, settings, [],
            new SettingsContext
            {
                Autostart = new UnsupportedAutostart(),
                Secrets = new UnavailableSecretStore(),
            });

        var row = Assert.Single(vm.RemoteEngines);
        Assert.False(row.HasProblem);
        Assert.Equal("not reachable", row.Status);
    }

    [Fact]
    public void An_empty_form_does_not_complain_about_options()
    {
        // "A host is required" is the empty-form state and belongs to the submit button being disabled,
        // not to a red line under a form nobody has filled in yet.
        var vm = Form();

        vm.RemoteHost = string.Empty;

        Assert.Null(vm.RemoteProblem);
        Assert.False(vm.CanAddRemote);
    }
}
