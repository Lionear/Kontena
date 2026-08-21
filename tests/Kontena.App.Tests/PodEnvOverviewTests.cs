using Kontena.App.ViewModels;
using Kontena.Core.Models;
using Kontena.Core.Orchestration.Fakes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.Tests;

/// <summary>
/// The environment section of the pod Overview tab (KON-416): what each container runs with, grouped
/// per container, and — for a variable that comes from a Secret — the value behind the eye.
/// </summary>
public sealed class PodEnvOverviewTests
{
    private static readonly TerminalFont Font = new("JetBrains Mono", 13, false);

    private static async Task<ClusterPodDetailViewModel> PageFor(string pod)
    {
        var cluster = new FakeClusterEngine();
        var pods = await cluster.ListPodsAsync("app");
        return new ClusterPodDetailViewModel(cluster, pods.First(p => p.Name == pod), Font);
    }

    /// <summary>
    /// A pod built here rather than seeded, because all five shapes on one container is not a pod
    /// anyone has — and every one of them renders differently.
    /// </summary>
    private static ClusterPodDetailViewModel PageForEveryShape()
    {
        var pod = new Pod
        {
            Name = "api-7d9c",
            Namespace = "app",
            Phase = PodPhase.Running,
            Containers =
            [
                new ContainerStatus
                {
                    Name = "api",
                    Image = "ghcr.io/lionear/api:1.8",
                    Ready = true,
                    RunState = ContainerRunState.Running,
                    Env =
                    [
                        new("LOG_LEVEL", "info", EnvSourceKind.Literal),
                        new("PGPASSWORD", string.Empty, EnvSourceKind.Secret, "postgres-credentials", "password"),
                        new("NGINX_LOG_LEVEL", string.Empty, EnvSourceKind.ConfigMap, "web-config", "LOG_LEVEL"),
                        new("POD_IP", string.Empty, EnvSourceKind.Field, SourceKey: "status.podIP"),
                        new("MEM_LIMIT", string.Empty, EnvSourceKind.Resource, "api", "limits.memory"),
                        new("CPU_LIMIT", string.Empty, EnvSourceKind.Resource, SourceKey: "limits.cpu"),
                    ],
                },
            ],
        };

        return new ClusterPodDetailViewModel(new FakeClusterEngine(), pod, Font);
    }

    [Fact]
    public async Task The_environment_is_on_the_tab_you_land_on()
    {
        using var page = await PageFor("api-7d9c");

        Assert.True(page.HasEnv);
        Assert.Equal(["LOG_LEVEL", "PGPASSWORD", "POD_IP"], page.EnvGroups.Single().Rows.Select(r => r.Name));
    }

    /// <summary>A pod that declares nothing gets no section, rather than an empty heading.</summary>
    [Fact]
    public async Task A_pod_without_environment_has_no_section()
    {
        using var page = await PageFor("web-5f2a");

        Assert.False(page.HasEnv);
        Assert.Empty(page.EnvGroups);
    }

    /// <summary>
    /// The same name may hold different values in two containers, so the group is the container. On a
    /// multi-container pod its name is shown; on a single-container one it would only repeat the pod.
    /// </summary>
    [Fact]
    public async Task The_container_is_named_only_when_there_is_more_than_one()
    {
        using var multi = await PageFor("api-7d9c");
        using var single = PageForEveryShape();

        Assert.Equal("c0", multi.EnvGroups.Single().Container);
        Assert.True(multi.EnvGroups.Single().ShowContainer);
        Assert.False(single.EnvGroups.Single().ShowContainer);
    }

    [Fact]
    public void A_value_from_says_where_it_comes_from_instead_of_showing_nothing()
    {
        using var page = PageForEveryShape();
        var rows = page.EnvGroups.Single().Rows;

        Assert.Equal(
            [
                ("LOG_LEVEL", true, "info", ""),
                ("PGPASSWORD", false, "", "from secret postgres-credentials.password"),
                ("NGINX_LOG_LEVEL", false, "", "from configmap web-config.LOG_LEVEL"),
                ("POD_IP", false, "", "from field status.podIP"),
                ("MEM_LIMIT", false, "", "from resource limits.memory of api"),
                // A resourceFieldRef without a container name means "this one", which the row says by
                // leaving the clause off rather than by dangling an "of".
                ("CPU_LIMIT", false, "", "from resource limits.cpu"),
            ],
            rows.Select(r => (r.Name, r.IsLiteral, r.Value, r.SourceText)));
    }

    /// <summary>
    /// Only a Secret gets an eye. A literal is already on screen, and a ConfigMap key is a second call
    /// for something the Config &amp; secrets section below unfolds anyway.
    /// </summary>
    [Fact]
    public void Only_a_secret_backed_variable_offers_a_reveal()
    {
        using var page = PageForEveryShape();

        Assert.Equal(["PGPASSWORD"], page.EnvGroups.Single().Rows.Where(r => r.IsSecret).Select(r => r.Name));
    }

    /// <summary>
    /// The eye reaches the real value, and pressing it again drops it — the rule the config page keeps
    /// and the reason this row borrows that page's reveal whole rather than growing its own.
    /// </summary>
    [Fact]
    public async Task The_eye_shows_the_value_behind_the_reference_and_takes_it_back()
    {
        using var page = await PageFor("api-7d9c");
        var row = page.EnvGroups.Single().Rows.Single(r => r.Name == "PGPASSWORD");

        Assert.NotNull(row.Secret);
        Assert.Null(row.Secret.Value);

        // The eye's accessible name names the variable: this page carries one in two sections now.
        Assert.Equal("Show the value of PGPASSWORD", row.ShowTip);
        Assert.Equal("Hide the value of PGPASSWORD", row.HideTip);

        await row.Secret.ToggleCommand.ExecuteAsync(null);

        Assert.True(row.Secret.IsRevealed);
        Assert.Equal("s3cr3t-but-not-really", row.Secret.Value);

        await row.Secret.ToggleCommand.ExecuteAsync(null);

        Assert.False(row.Secret.IsRevealed);
        Assert.Null(row.Secret.Value);
    }
}
