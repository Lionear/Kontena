using System.Text.Json;
using System.Text.Json.Serialization;
using Kontena.Sdk.Models;
using Xunit;
using Kontena.Core.Models;

namespace Kontena.Core.Tests;

public class KontenaSettingsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Defaults_are_dark_and_auto_detecting()
    {
        var settings = new KontenaSettings();
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.True(settings.AutoDetectEngines);
        Assert.Null(settings.DefaultEngine);
    }

    [Fact]
    public void Update_channel_is_stored_by_name_so_a_new_channel_can_be_slotted_in()
    {
        // Preview was added between Stable and Nightly, which renumbered Nightly. That is only safe
        // because the file holds names: a settings file written before the insert must still come
        // back as Nightly, not as whatever now sits at its old ordinal.
        var restored = JsonSerializer.Deserialize<KontenaSettings>(
            """{"UpdateChannel": "Nightly"}""", Options);

        Assert.Equal(UpdateChannel.Nightly, restored!.UpdateChannel);
    }

    [Fact]
    public void An_unchosen_channel_follows_the_build_it_came_from()
    {
        // Downloading a nightly is itself the choice, and answering it with "actually, stable" on first
        // launch overrules the user. The rule that stops drift is elsewhere: a *stored* choice wins,
        // so an install never moves onto a rolling stream by itself (KON-110, KON-123).
        var fresh = new KontenaSettings();

        Assert.Null(fresh.UpdateChannel);
        Assert.Equal(UpdateChannel.Nightly, fresh.ResolvedUpdateChannel(UpdateChannel.Nightly));
        Assert.Equal(UpdateChannel.Preview, fresh.ResolvedUpdateChannel(UpdateChannel.Preview));
        Assert.Equal(UpdateChannel.Stable, fresh.ResolvedUpdateChannel(UpdateChannel.Stable));
    }

    [Fact]
    public void A_chosen_channel_beats_the_build()
    {
        // The whole point of storing it: a nightly build whose user asked for stable stays on stable,
        // and a stable install that opted into nightly is not dragged back.
        var chosen = new KontenaSettings { UpdateChannel = UpdateChannel.Stable };
        Assert.Equal(UpdateChannel.Stable, chosen.ResolvedUpdateChannel(UpdateChannel.Nightly));

        var opted = new KontenaSettings { UpdateChannel = UpdateChannel.Nightly };
        Assert.Equal(UpdateChannel.Nightly, opted.ResolvedUpdateChannel(UpdateChannel.Stable));
    }

    [Fact]
    public void Updates_are_fetched_in_the_background_by_default()
    {
        var settings = new KontenaSettings();
        Assert.True(settings.AutoDownloadUpdates);
        Assert.Null(settings.DismissedUpdateVersion);
    }

    [Fact]
    public void An_existing_install_keeps_the_channel_already_in_its_file()
    {
        // Before KON-123 the field was not nullable, so every settings file on disk already spells out
        // a channel. That counts as chosen, which is what makes this change drift-free.
        var restored = JsonSerializer.Deserialize<KontenaSettings>(
            """{"UpdateChannel": "Stable"}""", Options);

        Assert.Equal(UpdateChannel.Stable, restored!.ResolvedUpdateChannel(UpdateChannel.Nightly));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var original = new KontenaSettings
        {
            Theme = ThemePreference.System,
            AutoDetectEngines = false,
            DefaultEngine = "podman",
            LaunchAtLogin = true,
            UpdateChannel = UpdateChannel.Nightly,
            AutoDownloadUpdates = false,
            DismissedUpdateVersion = "0.3.0",
            Registries =
            [
                new RegistryLogin("ghcr.io", "octo", RegistryCredentialSource.Kontena),
                new RegistryLogin("registry.local:5000", "ci", RegistryCredentialSource.Kontena),
            ],
            RemoteEngines =
            [
                new RemoteEngine("r1", "Build server", RemoteEngineTransport.Ssh, "build-01", User: "deploy"),
                new RemoteEngine("r2", "Lab", RemoteEngineTransport.Tcp, "lab.local", 2376,
                    CertificateDirectory: "/srv/docker/certs/lab"),
            ],
            KubeconfigPaths = ["/srv/kubeconfigs/acme.yaml", "~/Downloads/kubeconfig"],
            AllowedPlugins = ["com.acme.nerdctl@1.0.0", "com.acme.nerdctl@1.1.0"],
            DisabledAdapters = ["podman", "kubernetes"],
            AllowedExecCredentials =
            [
                "gke-prod#gke-gcloud-auth-plugin",
                "eks-staging#aws eks get-token --cluster-name staging",
            ],
            BackendNames = new Dictionary<string, string>
            {
                ["kubernetes:gke_myproject-prod_europe-west4_cluster-1"] = "Production EU",
                ["docker-remote:r1"] = "Build server",
            },
            KnownClusters = new Dictionary<string, bool>
            {
                ["kubernetes:gke_myproject-prod_europe-west4_cluster-1"] = true,
                ["kubernetes:kind-kind"] = false,
            },
            TerminalLigatures = true,
            ContainerGrouping = new Dictionary<string, bool>
            {
                ["docker"] = false,
                ["docker-remote:r1"] = true,
            },
            Shortcuts = new Dictionary<string, string>
            {
                ["page.refresh"] = "Ctrl+Shift+R",
            },
            RecentBuildContexts = ["/srv/build/app", "/srv/build/api"],
            PortForwards = new Dictionary<string, IReadOnlyList<RememberedPortForward>>
            {
                ["kubernetes:kind-kind"] =
                [
                    new RememberedPortForward("", "v1", "Service", "app", "api", "api · app", 80, 8080),
                    new RememberedPortForward("", "v1", "Pod", "app", "web-0", "web-0 · app", 8080, 9229),
                ],
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<KontenaSettings>(json, Options);

        Assert.NotNull(restored);
        // The list and the dictionary are reference-typed members (record equality won't compare
        // their contents), so verify them by sequence, then compare the scalar members with both
        // aligned.
        Assert.Equal(original.RecentBuildContexts, restored!.RecentBuildContexts);

        // Hosts and usernames survive; the secrets were never in here to begin with — they live in the
        // keychain, which is the point of storing only this much.
        Assert.Equal(original.Registries, restored.Registries);
        // Transport, host, port and the certificate path survive; the secrets never lived here.
        Assert.Equal(original.RemoteEngines, restored.RemoteEngines);

        // Paths only — a kubeconfig is read where it lies and never copied into settings.
        Assert.Equal(original.KubeconfigPaths, restored.KubeconfigPaths);

        // Consent is recorded per id and version independently, so both entries must survive.
        Assert.Equal(original.AllowedPlugins, restored.AllowedPlugins);

        // Switched-off adapters are stored as deviations (KON-283), so an entry that does not survive is
        // an adapter that quietly comes back on.
        Assert.Equal(original.DisabledAdapters, restored.DisabledAdapters);

        // The same for a kubeconfig credential command (KON-365): the command is part of the entry, and
        // one carrying spaces has to come back as the one string it went in as.
        Assert.Equal(original.AllowedExecCredentials, restored.AllowedExecCredentials);
        Assert.True(restored.AllowsExecCredential("eks-staging", "aws eks get-token --cluster-name staging"));

        Assert.Equal(original.BackendNames, restored.BackendNames);

        // Declined clusters survive as false, which is what stops them being offered again.
        Assert.Equal(original.KnownClusters, restored.KnownClusters);

        // Grouping turned off survives as false — absent means on, so the two must stay tellable apart.
        Assert.Equal(original.ContainerGrouping, restored.ContainerGrouping);

        // Only the shortcuts that were changed are in here; the rest follow the defaults (KON-180).
        Assert.Equal(original.Shortcuts, restored.Shortcuts);
        Assert.Equal(
            original.PortForwards["kubernetes:kind-kind"],
            restored.PortForwards["kubernetes:kind-kind"]);
        Assert.Equal(
            original with
            {
                RecentBuildContexts = restored.RecentBuildContexts,
                PortForwards = restored.PortForwards,
                Registries = restored.Registries,
                RemoteEngines = restored.RemoteEngines,
                KubeconfigPaths = restored.KubeconfigPaths,
                AllowedPlugins = restored.AllowedPlugins,
                DisabledAdapters = restored.DisabledAdapters,
                AllowedExecCredentials = restored.AllowedExecCredentials,
                BackendNames = restored.BackendNames,
                KnownClusters = restored.KnownClusters,
                ContainerGrouping = restored.ContainerGrouping,
                Shortcuts = restored.Shortcuts,
            },
            restored);
    }

    [Fact]
    public void Demo_backends_default_to_unset_so_the_build_decides()
    {
        // Null is not the same as false here: it means "not chosen", which lets a debug build show
        // demo backends while a release build reading the same file does not.
        Assert.Null(new KontenaSettings().ShowDemoBackends);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Demo_backends_preference_round_trips_including_unset(bool? preference)
    {
        var original = new KontenaSettings { ShowDemoBackends = preference };

        var restored = JsonSerializer.Deserialize<KontenaSettings>(JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(preference, restored!.ShowDemoBackends);
    }

    // ── Startup backend (KON-98) ─────────────────────────────────────────────

    [Fact]
    public void A_fresh_install_continues_where_you_left_off()
    {
        var settings = new KontenaSettings();

        Assert.Equal(StartupBackend.LastUsed, settings.ResolvedStartup);

        // Nothing has been used yet, so there is nothing to return to — first connected wins.
        Assert.Null(settings.StartupTarget);
    }

    [Fact]
    public void The_last_backend_is_what_launch_reopens()
    {
        var settings = new KontenaSettings { LastBackend = "kubernetes:kind-kind" };

        Assert.Equal("kubernetes:kind-kind", settings.StartupTarget);
    }

    [Fact]
    public void A_pinned_backend_beats_whatever_was_open_last()
    {
        var settings = new KontenaSettings
        {
            Startup = StartupBackend.Pinned,
            PinnedBackend = "docker",
            LastBackend = "kubernetes:kind-kind",
        };

        Assert.Equal("docker", settings.StartupTarget);
    }

    [Fact]
    public void First_connected_ignores_both()
    {
        var settings = new KontenaSettings
        {
            Startup = StartupBackend.FirstConnected,
            PinnedBackend = "docker",
            LastBackend = "kubernetes:kind-kind",
        };

        Assert.Null(settings.StartupTarget);
    }

    [Fact]
    public void A_settings_file_from_before_this_setting_keeps_its_chosen_engine()
    {
        // DefaultEngine was an explicit choice; an upgrade must not quietly turn it into
        // "last used" and start following the user around instead.
        var legacy = new KontenaSettings { DefaultEngine = "podman" };

        Assert.Equal(StartupBackend.Pinned, legacy.ResolvedStartup);
        Assert.Equal("podman", legacy.StartupTarget);
    }

    [Fact]
    public void A_legacy_file_that_never_chose_an_engine_gets_the_new_default()
    {
        var legacy = new KontenaSettings { DefaultEngine = null, LastBackend = "docker" };

        Assert.Equal(StartupBackend.LastUsed, legacy.ResolvedStartup);
        Assert.Equal("docker", legacy.StartupTarget);
    }

    [Fact]
    public void An_explicit_choice_wins_over_the_legacy_field()
    {
        var settings = new KontenaSettings
        {
            Startup = StartupBackend.LastUsed,
            DefaultEngine = "podman",
            LastBackend = "docker",
        };

        Assert.Equal("docker", settings.StartupTarget);
    }

    [Fact]
    public void Startup_preference_round_trips_including_unset()
    {
        var original = new KontenaSettings
        {
            Startup = StartupBackend.Pinned,
            PinnedBackend = "kubernetes:prod",
            LastBackend = "docker",
        };

        var restored = JsonSerializer.Deserialize<KontenaSettings>(JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(StartupBackend.Pinned, restored!.Startup);
        Assert.Equal("kubernetes:prod", restored.PinnedBackend);
        Assert.Equal("docker", restored.LastBackend);

        // Absent in the file means "never chosen", which is what the migration keys off.
        Assert.Null(JsonSerializer.Deserialize<KontenaSettings>("{}", Options)!.Startup);
    }

    [Fact]
    public void Theme_serializes_as_a_name_not_a_number()
    {
        var json = JsonSerializer.Serialize(new KontenaSettings { Theme = ThemePreference.Light }, Options);
        Assert.Contains("\"Light\"", json);
    }

    [Fact]
    public void Off_is_a_value_of_the_alert_refresh_interval_not_a_flag_beside_it()
    {
        // A bool and an interval can contradict each other; one field cannot (KON-393).
        Assert.Null(AlertRefresh.Interval(0));
        Assert.Null(AlertRefresh.Interval(-1));
        Assert.Equal(TimeSpan.FromSeconds(30), AlertRefresh.Interval(30));
        Assert.Equal("Off", AlertRefresh.Label(0));

        Assert.Equal(AlertRefresh.DefaultSeconds, new KontenaSettings().AlertRefreshSeconds);
        Assert.Contains(AlertRefresh.DefaultSeconds, AlertRefresh.Choices);
        Assert.Equal(0, AlertRefresh.Choices[0]);
    }

    [Fact]
    public void A_hand_edited_interval_is_clamped_rather_than_trusted()
    {
        // The file is JSON a person can open, and this value becomes a poll against somebody's
        // Alertmanager. One second is not on the picker, so it is not an answer we honour.
        Assert.Equal(TimeSpan.FromSeconds(5), AlertRefresh.Interval(1));
        Assert.Equal(TimeSpan.FromHours(1), AlertRefresh.Interval(86_400));

        // Shown as what it says, though — snapping 45 to 30 in the picker would have Settings claim
        // something the Alerts page is not doing.
        Assert.Equal("Every 45 seconds", AlertRefresh.Label(45));
        Assert.Equal("Every minute", AlertRefresh.Label(60));
        Assert.Equal("Every 5 minutes", AlertRefresh.Label(300));
    }
}
