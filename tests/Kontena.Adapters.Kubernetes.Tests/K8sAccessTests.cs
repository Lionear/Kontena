using k8s.Models;
using Kontena.Adapters.Kubernetes;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>The RBAC mappers behind the access page (KON-474).</summary>
public class K8sAccessTests
{
    [Fact]
    public void A_role_binding_keeps_its_role_reference_and_every_subject()
    {
        var b = K8sMap.ToAccessBinding(new V1RoleBinding
        {
            Metadata = new V1ObjectMeta { Name = "ci-view", NamespaceProperty = "app" },
            RoleRef = new V1RoleRef { ApiGroup = "rbac.authorization.k8s.io", Kind = "ClusterRole", Name = "view" },
            Subjects =
            [
                new Rbacv1Subject { Kind = "ServiceAccount", Name = "ci", NamespaceProperty = "app" },
                new Rbacv1Subject { Kind = "User", Name = "jane" },
            ],
        });

        Assert.Equal("app", b.Namespace);
        Assert.False(b.IsClusterBinding);
        Assert.Equal(("ClusterRole", "view"), (b.RoleKind, b.RoleName));
        Assert.Equal("app", b.Subjects[0].Namespace);
        Assert.Null(b.Subjects[1].Namespace);
    }

    [Fact]
    public void A_cluster_role_has_no_namespace_and_keeps_its_rules()
    {
        var r = K8sMap.ToAccessRole(new V1ClusterRole
        {
            Metadata = new V1ObjectMeta { Name = "reader" },
            Rules =
            [
                new V1PolicyRule { ApiGroups = ["apps"], Resources = ["deployments"], Verbs = ["get", "list"] },
                new V1PolicyRule { NonResourceURLs = ["/healthz"], Verbs = ["get"] },
            ],
        });

        Assert.True(r.IsClusterRole);
        Assert.Equal(["deployments"], r.Rules[0].Resources);
        Assert.Equal(["/healthz"], r.Rules[1].NonResourceUrls);
    }

    [Fact]
    public void A_cluster_role_binding_with_no_subjects_maps_to_an_empty_list()
    {
        var b = K8sMap.ToAccessBinding(new V1ClusterRoleBinding
        {
            Metadata = new V1ObjectMeta { Name = "orphan" },
            RoleRef = new V1RoleRef { Kind = "ClusterRole", Name = "view" },
        });

        Assert.True(b.IsClusterBinding);
        Assert.Empty(b.Subjects);
    }
}
