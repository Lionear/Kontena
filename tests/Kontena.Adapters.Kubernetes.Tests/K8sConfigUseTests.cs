using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Which ConfigMaps and Secrets a pod reads (KON-330) — read off the spec that came with the listing,
/// so a secret's "Used by" costs no extra call.
/// <para>
/// Worth pinning because a spec reaches configuration from four directions and missing one is
/// invisible: the tab would simply show fewer pods, and "nothing uses this" is exactly the answer
/// someone acts on when they delete it.
/// </para>
/// </summary>
public class K8sConfigUseTests
{
    private static V1Pod Pod(V1PodSpec spec) => new()
    {
        Metadata = new V1ObjectMeta { Name = "api-1", NamespaceProperty = "app", CreationTimestamp = DateTime.UtcNow },
        Spec = spec,
        Status = new V1PodStatus { Phase = "Running" },
    };

    private static V1Container Container(
        string name = "api", IList<V1EnvVar>? env = null, IList<V1EnvFromSource>? envFrom = null) =>
        new() { Name = name, Image = "ghcr.io/lionear/api:1", Env = env, EnvFrom = envFrom };

    [Fact]
    public void A_mounted_secret_and_config_map_are_both_found()
    {
        var pod = Pod(new V1PodSpec
        {
            Containers = [Container()],
            Volumes =
            [
                new V1Volume { Name = "tls", Secret = new V1SecretVolumeSource { SecretName = "app-tls" } },
                new V1Volume { Name = "conf", ConfigMap = new V1ConfigMapVolumeSource { Name = "web-config" } },
            ],
        });

        var uses = K8sMap.ToPod(pod).ConfigUses;

        Assert.Contains(uses, u => u.Kind == GroupVersionKind.Secret && u.Name == "app-tls" && u.How == ConfigUseKind.Volume);
        Assert.Contains(uses, u => u.Kind == GroupVersionKind.ConfigMap && u.Name == "web-config" && u.How == ConfigUseKind.Volume);
    }

    /// <summary>A projected volume is how a modern pod mounts a token beside a secret, and it hides the
    /// reference one level deeper than the plain sources above.</summary>
    [Fact]
    public void A_projected_source_counts_as_a_mount()
    {
        var pod = Pod(new V1PodSpec
        {
            Containers = [Container()],
            Volumes =
            [
                new V1Volume
                {
                    Name = "all",
                    Projected = new V1ProjectedVolumeSource
                    {
                        Sources =
                        [
                            new V1VolumeProjection { Secret = new V1SecretProjection { Name = "app-tls" } },
                            new V1VolumeProjection { ConfigMap = new V1ConfigMapProjection { Name = "kube-root-ca.crt" } },
                        ],
                    },
                },
            ],
        });

        var uses = K8sMap.ToPod(pod).ConfigUses;

        Assert.Contains(uses, u => u.Name == "app-tls" && u.How == ConfigUseKind.Volume);
        Assert.Contains(uses, u => u.Name == "kube-root-ca.crt" && u.How == ConfigUseKind.Volume);
    }

    [Fact]
    public void Environment_from_one_key_and_from_a_whole_object_are_told_apart()
    {
        var pod = Pod(new V1PodSpec
        {
            Containers =
            [
                Container(
                    env:
                    [
                        new V1EnvVar
                        {
                            Name = "PGPASSWORD",
                            ValueFrom = new V1EnvVarSource
                            {
                                SecretKeyRef = new V1SecretKeySelector { Name = "postgres-credentials", Key = "password" },
                            },
                        },
                    ],
                    envFrom: [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "web-config" } }]),
            ],
        });

        var uses = K8sMap.ToPod(pod).ConfigUses;

        Assert.Contains(uses, u =>
            u.Name == "postgres-credentials" && u.How == ConfigUseKind.EnvironmentVariable && u.Container == "api");
        Assert.Contains(uses, u =>
            u.Name == "web-config" && u.How == ConfigUseKind.EnvironmentFrom && u.Container == "api");
    }

    /// <summary>A secret only an init container reads is still a secret whose removal stops the pod
    /// from starting.</summary>
    [Fact]
    public void An_init_container_counts()
    {
        var pod = Pod(new V1PodSpec
        {
            Containers = [Container()],
            InitContainers =
            [
                Container("wait-for-db", envFrom:
                [
                    new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = "postgres-credentials" } },
                ]),
            ],
        });

        var uses = K8sMap.ToPod(pod).ConfigUses;

        Assert.Contains(uses, u => u.Name == "postgres-credentials" && u.Container == "wait-for-db");
    }

    [Fact]
    public void An_image_pull_secret_is_a_use_with_no_container_behind_it()
    {
        var pod = Pod(new V1PodSpec
        {
            Containers = [Container()],
            ImagePullSecrets = [new V1LocalObjectReference { Name = "ghcr-pull" }],
        });

        var use = Assert.Single(K8sMap.ToPod(pod).ConfigUses);

        Assert.Equal("ghcr-pull", use.Name);
        Assert.Equal(ConfigUseKind.ImagePullSecret, use.How);
        Assert.Equal(string.Empty, use.Container);
    }

    /// <summary>
    /// The same secret mounted into two containers is one answer to "is this in use". The tab lists
    /// pods, not mounts, so a pod that appeared twice would have read as two.
    /// </summary>
    [Fact]
    public void The_same_reference_twice_is_reported_once()
    {
        var envFrom = new List<V1EnvFromSource>
        {
            new() { SecretRef = new V1SecretEnvSource { Name = "postgres-credentials" } },
        };

        var pod = Pod(new V1PodSpec
        {
            Containers = [Container("api", envFrom: envFrom), Container("api", envFrom: envFrom)],
        });

        Assert.Single(K8sMap.ToPod(pod).ConfigUses);
    }

    [Fact]
    public void A_pod_that_reads_nothing_reports_nothing()
    {
        Assert.Empty(K8sMap.ToPod(Pod(new V1PodSpec { Containers = [Container()] })).ConfigUses);
    }
}
