using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The generic "what uses this" ladder (KON-455). The point of every case here is that it holds for a
/// kind nobody taught the app about — the moment this needs to know what a Dragonfly is, it stops
/// working for the next operator someone installs.
/// </summary>
public sealed class ResourceUsersTests
{
    private static readonly ResourceRef Target =
        new(new GroupVersionKind("dragonflydb.io", "v1alpha1", "Dragonfly"), "data", "cache");

    private static V1OwnerReference Owner(string kind, string name, string apiVersion) =>
        new() { Kind = kind, Name = name, ApiVersion = apiVersion };

    // ── ownerReference ──────────────────────────────────────────────────────

    [Fact]
    public void An_owner_reference_to_the_object_is_a_link()
    {
        Assert.True(ResourceUsers.OwnedBy(
            [Owner("Dragonfly", "cache", "dragonflydb.io/v1alpha1")], Target));
    }

    /// <summary>
    /// Same kind and name in a different group is a different object. Two operators using the same
    /// obvious noun is not a coincidence worth guessing about.
    /// </summary>
    [Fact]
    public void An_owner_reference_from_another_group_is_not()
    {
        Assert.False(ResourceUsers.OwnedBy(
            [Owner("Dragonfly", "cache", "someone.else.io/v1")], Target));
    }

    [Fact]
    public void An_owner_reference_to_another_name_is_not()
    {
        Assert.False(ResourceUsers.OwnedBy(
            [Owner("Dragonfly", "other", "dragonflydb.io/v1alpha1")], Target));
    }

    [Fact]
    public void No_owner_references_at_all_is_handled()
    {
        Assert.False(ResourceUsers.OwnedBy(null, Target));
        Assert.False(ResourceUsers.OwnedBy([], Target));
    }

    // ── annotation ──────────────────────────────────────────────────────────

    /// <summary>The key has to belong to the object's own group, or this matches half the cluster.</summary>
    [Fact]
    public void An_annotation_under_the_objects_group_names_it()
    {
        var key = ResourceUsers.AnnotationNaming(
            new Dictionary<string, string> { ["dragonflydb.io/instance"] = "cache" }, Target);

        Assert.Equal("dragonflydb.io/instance", key);
    }

    [Fact]
    public void An_annotation_under_any_other_key_does_not()
    {
        Assert.Null(ResourceUsers.AnnotationNaming(
            new Dictionary<string, string> { ["team"] = "cache" }, Target));
    }

    [Fact]
    public void An_annotation_naming_something_else_does_not()
    {
        Assert.Null(ResourceUsers.AnnotationNaming(
            new Dictionary<string, string> { ["dragonflydb.io/instance"] = "other" }, Target));
    }

    /// <summary>A core-group object has no group to qualify a key with, so this rung does not apply.</summary>
    [Fact]
    public void A_core_group_object_gets_no_annotation_match()
    {
        var core = new ResourceRef(GroupVersionKind.ConfigMap, "data", "cache");

        Assert.Null(ResourceUsers.AnnotationNaming(
            new Dictionary<string, string> { ["anything"] = "cache" }, core));
    }

    // ── config references out of a pod spec ─────────────────────────────────

    [Fact]
    public void A_secret_volume_is_a_reference()
    {
        var spec = new V1PodSpec
        {
            Containers = [],
            Volumes = [new V1Volume { Name = "creds", Secret = new V1SecretVolumeSource { SecretName = "cache-auth" } }],
        };

        Assert.Contains("Secret/cache-auth", ResourceUsers.ConfigReferences(spec));
    }

    [Fact]
    public void An_env_from_reference_counts()
    {
        var spec = new V1PodSpec
        {
            Containers =
            [
                new()
                {
                    Name = "app",
                    EnvFrom = [new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = "cache-auth" } }],
                },
            ],
        };

        Assert.Contains("Secret/cache-auth", ResourceUsers.ConfigReferences(spec));
    }

    [Fact]
    public void A_single_value_from_variable_counts()
    {
        var spec = new V1PodSpec
        {
            Containers =
            [
                new()
                {
                    Name = "app",
                    Env =
                    [
                        new()
                        {
                            Name = "PASSWORD",
                            ValueFrom = new V1EnvVarSource
                            {
                                SecretKeyRef = new V1SecretKeySelector { Name = "cache-auth", Key = "password" },
                            },
                        },
                    ],
                },
            ],
        };

        Assert.Contains("Secret/cache-auth", ResourceUsers.ConfigReferences(spec));
    }

    /// <summary>
    /// A workload that only reads the credentials while starting up still stops working when the
    /// object behind them goes away.
    /// </summary>
    [Fact]
    public void An_init_container_counts_too()
    {
        var spec = new V1PodSpec
        {
            Containers = [new() { Name = "app" }],
            InitContainers =
            [
                new()
                {
                    Name = "migrate",
                    EnvFrom = [new V1EnvFromSource { ConfigMapRef = new V1ConfigMapEnvSource { Name = "cache-conf" } }],
                },
            ],
        };

        Assert.Contains("ConfigMap/cache-conf", ResourceUsers.ConfigReferences(spec));
    }

    [Fact]
    public void A_projected_volume_counts_too()
    {
        var spec = new V1PodSpec
        {
            Containers = [],
            Volumes =
            [
                new()
                {
                    Name = "all",
                    Projected = new V1ProjectedVolumeSource
                    {
                        Sources = [new V1VolumeProjection { Secret = new V1SecretProjection { Name = "cache-auth" } }],
                    },
                },
            ],
        };

        Assert.Contains("Secret/cache-auth", ResourceUsers.ConfigReferences(spec));
    }

    [Fact]
    public void An_empty_spec_references_nothing()
    {
        Assert.Empty(ResourceUsers.ConfigReferences(null));
        Assert.Empty(ResourceUsers.ConfigReferences(new V1PodSpec { Containers = [] }));
    }

    // ── the ladder as a whole ───────────────────────────────────────────────

    private static readonly ResourceRef Workload =
        new(GroupVersionKind.Deployment, "shop", "checkout-api");

    private static V1PodSpec MountingSecret(string name) => new()
    {
        Containers =
        [
            new() { Name = "app", EnvFrom = [new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = name } }] },
        ],
    };

    [Fact]
    public void A_mounted_secret_the_object_owns_is_a_link_and_says_which_secret()
    {
        var usage = ResourceUsers.Link(
            Workload, owners: null, annotations: null, MountingSecret("cache-auth"), Target,
            new Dictionary<string, string> { ["Secret/cache-auth"] = "Secret cache-auth" });

        Assert.NotNull(usage);
        Assert.Equal(UsageEvidence.Mount, usage.Evidence);
        Assert.Equal("Secret cache-auth", usage.Detail);
        Assert.False(usage.OwnedByTarget);
    }

    /// <summary>A secret with the right name that this object does not own is somebody else's.</summary>
    [Fact]
    public void A_mounted_secret_the_object_does_not_own_is_not_a_link()
    {
        var usage = ResourceUsers.Link(
            Workload, owners: null, annotations: null, MountingSecret("cache-auth"), Target,
            new Dictionary<string, string>());

        Assert.Null(usage);
    }

    /// <summary>
    /// Both directions live in one list and they do not mean the same thing: an object the custom
    /// resource created is flagged, one that merely refers to it is not.
    /// </summary>
    [Fact]
    public void An_owned_workload_is_marked_as_owned_rather_than_as_a_user()
    {
        var usage = ResourceUsers.Link(
            Workload, [Owner("Dragonfly", "cache", "dragonflydb.io/v1alpha1")],
            annotations: null, spec: null, Target, new Dictionary<string, string>());

        Assert.NotNull(usage);
        Assert.Equal(UsageEvidence.OwnerReference, usage.Evidence);
        Assert.True(usage.OwnedByTarget);
    }

    /// <summary>The strongest available answer wins, so a workload that both owns and mounts says so once.</summary>
    [Fact]
    public void The_strongest_evidence_is_the_one_reported()
    {
        var usage = ResourceUsers.Link(
            Workload, [Owner("Dragonfly", "cache", "dragonflydb.io/v1alpha1")],
            new Dictionary<string, string> { ["dragonflydb.io/instance"] = "cache" },
            MountingSecret("cache-auth"), Target,
            new Dictionary<string, string> { ["Secret/cache-auth"] = "Secret cache-auth" });

        Assert.Equal(UsageEvidence.OwnerReference, usage!.Evidence);
    }

    [Fact]
    public void A_workload_with_nothing_pointing_at_the_object_is_not_listed()
    {
        Assert.Null(ResourceUsers.Link(
            Workload, owners: null, annotations: null, spec: new V1PodSpec { Containers = [] },
            Target, new Dictionary<string, string>()));
    }
}
