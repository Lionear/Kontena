using k8s.Models;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The volume and storage-class mappers (KON-254). Two fields here decide whether someone's data
/// survives, and one of them is not a field at all.
/// </summary>
public class K8sStorageTests
{
    [Fact]
    public void A_reclaim_policy_the_cluster_did_not_state_is_Delete()
    {
        // Kubernetes' own default, and the direction that loses data — so it is the one place a
        // guess has to match the cluster exactly rather than being cautious.
        var v = K8sMap.ToVolume(new V1PersistentVolume { Spec = new V1PersistentVolumeSpec() });

        Assert.Equal(ReclaimPolicy.Delete, v.ReclaimPolicy);
    }

    [Fact]
    public void The_claim_is_qualified_by_its_namespace()
    {
        // A claim name alone is ambiguous across namespaces, and this column exists to be matched
        // against the claims list.
        var v = K8sMap.ToVolume(new V1PersistentVolume
        {
            Spec = new V1PersistentVolumeSpec
            {
                ClaimRef = new V1ObjectReference { Name = "postgres-data", NamespaceProperty = "app" },
            },
        });

        Assert.Equal("app/postgres-data", v.Claim);
    }

    [Fact]
    public void An_unbound_volume_names_no_claim()
    {
        Assert.Equal(string.Empty, K8sMap.ToVolume(new V1PersistentVolume()).Claim);
    }

    [Theory]
    [InlineData("hostPath")]
    [InlineData("local")]
    public void An_in_tree_source_is_named_even_though_it_reports_no_driver(string expected)
    {
        // CSI states its driver; the in-tree sources do not, so the source that is set is the answer.
        // Worth having: "hostPath" is the whole story on a kind or minikube cluster.
        var spec = new V1PersistentVolumeSpec();
        if (expected == "hostPath")
            spec.HostPath = new V1HostPathVolumeSource { Path = "/data" };
        else
            spec.Local = new V1LocalVolumeSource { Path = "/data" };

        Assert.Equal(expected, K8sMap.ToVolume(new V1PersistentVolume { Spec = spec }).Driver);
    }

    [Fact]
    public void Csi_wins_over_everything_because_it_states_itself()
    {
        var v = K8sMap.ToVolume(new V1PersistentVolume
        {
            Spec = new V1PersistentVolumeSpec
            {
                Csi = new V1CSIPersistentVolumeSource { Driver = "ebs.csi.aws.com", VolumeHandle = "vol-1" },
            },
        });

        Assert.Equal("ebs.csi.aws.com", v.Driver);
    }

    [Theory]
    [InlineData("storageclass.kubernetes.io/is-default-class")]
    [InlineData("storageclass.beta.kubernetes.io/is-default-class")]
    public void Both_spellings_of_the_default_annotation_count(string key)
    {
        // The default is an annotation and not a field, and the beta spelling is still in place on
        // every cluster that was upgraded rather than rebuilt. Reading only the current one would
        // show such a cluster as having no default at all.
        var c = K8sMap.ToStorageClass(new V1StorageClass
        {
            Metadata = new V1ObjectMeta
            {
                Name = "standard",
                Annotations = new Dictionary<string, string> { [key] = "true" },
            },
            Provisioner = "kubernetes.io/gce-pd",
        });

        Assert.True(c.IsDefault);
    }

    [Fact]
    public void An_annotation_set_to_false_is_not_a_default()
    {
        // Present-and-false is a real configuration, and reading the key's presence would invert it.
        var c = K8sMap.ToStorageClass(new V1StorageClass
        {
            Metadata = new V1ObjectMeta
            {
                Name = "standard",
                Annotations = new Dictionary<string, string>
                {
                    ["storageclass.kubernetes.io/is-default-class"] = "false",
                },
            },
        });

        Assert.False(c.IsDefault);
    }

    [Fact]
    public void The_binding_mode_defaults_to_Immediate()
    {
        // Which is what Kubernetes does when the field is absent.
        Assert.Equal(VolumeBindingMode.Immediate, K8sMap.ToStorageClass(new V1StorageClass()).BindingMode);

        Assert.Equal(
            VolumeBindingMode.WaitForFirstConsumer,
            K8sMap.ToStorageClass(new V1StorageClass { VolumeBindingMode = "WaitForFirstConsumer" }).BindingMode);
    }
}
