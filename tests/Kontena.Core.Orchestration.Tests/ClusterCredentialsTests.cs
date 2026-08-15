using Kontena.Sdk.Orchestration.Provisioning;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

public class ClusterCredentialsTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"kontena-cred-{Guid.NewGuid():N}")).FullName;

    private string Key(string name = "id_ed25519")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not really a key");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go is the operating system's problem, not the test's.
        }

        GC.SuppressFinalize(this);
    }

    // ── SSH ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_user_and_a_key_that_exists_is_usable()
    {
        var credentials = new SshCredentials("rick") { KeyPath = Key() };

        Assert.Null(credentials.Problem());
        Assert.Equal(ProvisionerTransport.Ssh, credentials.Transport);
    }

    [Fact]
    public void No_key_at_all_leans_on_the_agent_and_that_is_fine_when_it_has_something()
    {
        var credentials = new SshCredentials("rick");

        Assert.Null(credentials.Problem(["id_ed25519"]));
    }

    [Fact]
    public void No_key_and_an_agent_holding_nothing_says_so_here_rather_than_at_connect_time()
    {
        var problem = new SshCredentials("rick").Problem([]);

        Assert.NotNull(problem);
        Assert.Contains("ssh-add", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_nobody_asked_about_is_not_complained_about()
    {
        // null is "we did not look", which is different from "we looked and it was empty".
        Assert.Null(new SshCredentials("rick").Problem(agentKeys: null));
    }

    [Fact]
    public void A_key_path_that_does_not_exist_names_the_path()
    {
        var missing = Path.Combine(_dir, "nope");
        var problem = new SshCredentials("rick") { KeyPath = missing }.Problem();

        Assert.Contains(missing, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_public_half_is_caught_because_ssh_would_blame_the_far_machine_for_it()
    {
        var problem = new SshCredentials("rick") { KeyPath = Key("id_ed25519.pub") }.Problem();

        Assert.Contains("public half", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/pwned", null)]
    [InlineData(null, "-oProxyCommand=touch /tmp/pwned")]
    public void A_value_ssh_would_read_as_an_option_is_refused(string? user, string? keyPath)
    {
        var problem = new SshCredentials(user) { KeyPath = keyPath }.Problem();

        Assert.NotNull(problem);
        Assert.Contains("option", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Sudo_is_on_by_default_because_kubeadm_and_k0s_write_outside_a_home_directory()
    {
        Assert.True(new SshCredentials("rick").UseSudo);
    }

    // ── One key for all hosts, with a way out per host ───────────────────────

    [Fact]
    public void A_host_that_says_nothing_takes_the_clusters_key_and_user()
    {
        var cluster = new SshCredentials("rick") { KeyPath = "/keys/shared" };
        var host = new RemoteClusterHost("10.0.0.1", ClusterHostRole.Controller);

        var resolved = cluster.For(host);

        Assert.Equal("rick", resolved.User);
        Assert.Equal("/keys/shared", resolved.KeyPath);
    }

    [Fact]
    public void A_host_with_its_own_login_overrides_only_what_it_names()
    {
        var cluster = new SshCredentials("rick") { KeyPath = "/keys/shared" };
        var host = new RemoteClusterHost("10.0.0.1", ClusterHostRole.Worker) { User = "ubuntu" };

        var resolved = cluster.For(host);

        Assert.Equal("ubuntu", resolved.User);
        Assert.Equal("/keys/shared", resolved.KeyPath);
    }

    [Fact]
    public void A_host_can_differ_on_the_key_alone()
    {
        var cluster = new SshCredentials("rick") { KeyPath = "/keys/shared" };
        var host = new RemoteClusterHost("10.0.0.1", ClusterHostRole.Worker) { KeyPath = "/keys/odd-one" };

        var resolved = cluster.For(host);

        Assert.Equal("rick", resolved.User);
        Assert.Equal("/keys/odd-one", resolved.KeyPath);
    }

    [Fact]
    public void Resolving_for_a_host_leaves_the_clusters_own_credentials_alone()
    {
        var cluster = new SshCredentials("rick") { KeyPath = "/keys/shared" };
        cluster.For(new RemoteClusterHost("10.0.0.1", ClusterHostRole.Worker) { User = "ubuntu" });

        Assert.Equal("rick", cluster.User);
    }

    [Fact]
    public void Sudo_is_a_cluster_decision_and_survives_a_host_override()
    {
        var cluster = new SshCredentials("rick") { KeyPath = "/keys/shared", UseSudo = false };
        var resolved = cluster.For(new RemoteClusterHost("10.0.0.1", ClusterHostRole.Worker) { User = "root" });

        Assert.False(resolved.UseSudo);
    }

    // ── Talos ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_talosconfig_that_exists_is_usable_and_speaks_the_machine_api()
    {
        var credentials = new TalosCredentials { ConfigPath = Key("talosconfig") };

        Assert.Null(credentials.Problem());
        Assert.Equal(ProvisionerTransport.MachineApi, credentials.Transport);
    }

    [Fact]
    public void Talos_without_a_config_says_there_is_no_ssh_to_fall_back_on()
    {
        var problem = new TalosCredentials().Problem();

        Assert.NotNull(problem);
        Assert.Contains("talosconfig", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_talosconfig_path_that_does_not_exist_names_the_path()
    {
        var missing = Path.Combine(_dir, "nope");

        Assert.Contains(missing, new TalosCredentials { ConfigPath = missing }.Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_talosconfig_path_that_would_be_read_as_an_option_is_refused()
    {
        var problem = new TalosCredentials { ConfigPath = "-oSomething" }.Problem();

        Assert.Contains("option", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_talosconfig_kept_in_the_keychain_needs_no_path()
    {
        Assert.Null(new TalosCredentials { IsStored = true }.Problem());
    }

    [Fact]
    public void A_stored_config_and_a_file_at_once_is_two_answers_to_one_question()
    {
        var credentials = new TalosCredentials { IsStored = true, ConfigPath = Key("talosconfig") };

        Assert.NotNull(credentials.Problem());
    }

    [Fact]
    public void The_record_never_holds_the_config_itself_only_where_it_is()
    {
        // The guard is structural: if this type gains a contents property, this stops compiling.
        Assert.DoesNotContain(
            typeof(TalosCredentials).GetProperties(),
            p => p.Name.Contains("Content", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Neither_credential_type_has_anywhere_to_put_a_password()
    {
        foreach (var type in new[] { typeof(SshCredentials), typeof(TalosCredentials) })
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase));
        }
    }
}
