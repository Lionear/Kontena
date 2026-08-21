using k8s.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The environment variables a container declares (KON-416) — read off the spec that came with the
/// listing, and joined onto the status by container name the way ports and memory limits already are.
/// <para>
/// Worth pinning because only a literal <c>value:</c> is in the spec at all. Every other shape is a
/// reference, and mapping one of them to an empty value would put "set to nothing" on screen for a
/// variable that is very much set.
/// </para>
/// </summary>
public class K8sContainerEnvTests
{
    private static V1Pod Pod(V1PodSpec spec) => new()
    {
        Metadata = new V1ObjectMeta { Name = "api-1", NamespaceProperty = "app", CreationTimestamp = DateTime.UtcNow },
        Spec = spec,
        Status = new V1PodStatus
        {
            Phase = "Running",
            ContainerStatuses = [.. (spec.Containers ?? []).Select(c => new V1ContainerStatus
            {
                Name = c.Name, Image = c.Image, Ready = true, RestartCount = 0,
                State = new V1ContainerState { Running = new V1ContainerStateRunning() },
            })],
            InitContainerStatuses = [.. (spec.InitContainers ?? []).Select(c => new V1ContainerStatus
            {
                Name = c.Name, Image = c.Image, Ready = true, RestartCount = 0,
                State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } },
            })],
        },
    };

    private static V1Container Container(string name, params V1EnvVar[] env) =>
        new() { Name = name, Image = "ghcr.io/lionear/api:1", Env = env };

    private static V1EnvVar From(string name, V1EnvVarSource source) =>
        new() { Name = name, ValueFrom = source };

    [Fact]
    public void A_literal_value_comes_across_as_the_value()
    {
        var pod = Pod(new V1PodSpec { Containers = [Container("api", new V1EnvVar { Name = "LOG_LEVEL", Value = "info" })] });

        var env = Assert.Single(K8sMap.ToPod(pod).Containers[0].Env);

        Assert.Equal("LOG_LEVEL", env.Name);
        Assert.Equal("info", env.Value);
        Assert.Equal(EnvSourceKind.Literal, env.Source);
    }

    /// <summary>
    /// The four <c>valueFrom</c> shapes. None of them carries a value — the kubelet resolves them at
    /// start-up — so what has to survive the mapping is where to look.
    /// </summary>
    [Fact]
    public void Every_value_from_shape_keeps_where_the_value_comes_from()
    {
        var pod = Pod(new V1PodSpec
        {
            Containers =
            [
                Container(
                    "api",
                    From("PGPASSWORD", new V1EnvVarSource
                    {
                        SecretKeyRef = new V1SecretKeySelector { Name = "postgres-credentials", Key = "password" },
                    }),
                    From("LOG_LEVEL", new V1EnvVarSource
                    {
                        ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "web-config", Key = "LOG_LEVEL" },
                    }),
                    From("POD_IP", new V1EnvVarSource
                    {
                        FieldRef = new V1ObjectFieldSelector { FieldPath = "status.podIP" },
                    }),
                    From("MEM_LIMIT", new V1EnvVarSource
                    {
                        ResourceFieldRef = new V1ResourceFieldSelector { ContainerName = "api", Resource = "limits.memory" },
                    })),
            ],
        });

        var env = K8sMap.ToPod(pod).Containers[0].Env;

        Assert.All(env, e => Assert.Equal(string.Empty, e.Value));
        Assert.Equal(
            [
                ("PGPASSWORD", EnvSourceKind.Secret, "postgres-credentials", "password"),
                ("LOG_LEVEL", EnvSourceKind.ConfigMap, "web-config", "LOG_LEVEL"),
                ("POD_IP", EnvSourceKind.Field, "", "status.podIP"),
                ("MEM_LIMIT", EnvSourceKind.Resource, "api", "limits.memory"),
            ],
            env.Select(e => (e.Name, e.Source, e.SourceName, e.SourceKey)));
    }

    /// <summary>
    /// An init container's environment is not the app container's, and a pod wedged in its init
    /// container is exactly when someone comes looking for it.
    /// </summary>
    [Fact]
    public void Each_container_keeps_its_own_environment()
    {
        var pod = Pod(new V1PodSpec
        {
            InitContainers = [Container("wait-for-db", new V1EnvVar { Name = "DB_HOST", Value = "postgres" })],
            Containers = [Container("api", new V1EnvVar { Name = "LOG_LEVEL", Value = "info" })],
        });

        var mapped = K8sMap.ToPod(pod);

        Assert.Equal(["DB_HOST"], mapped.InitContainers[0].Env.Select(e => e.Name));
        Assert.Equal(["LOG_LEVEL"], mapped.Containers[0].Env.Select(e => e.Name));
    }

    /// <summary>A container that declares nothing has an empty list, not a missing one.</summary>
    [Fact]
    public void A_container_without_environment_reads_as_empty()
    {
        var pod = Pod(new V1PodSpec { Containers = [Container("api")] });

        Assert.Empty(K8sMap.ToPod(pod).Containers[0].Env);
    }
}
