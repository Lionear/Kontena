using System.Text.Json;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Reading the Table the API server renders (KON-75).
/// <para>
/// This is the whole reason the browser needs no model per kind: the server names the columns, so a
/// custom resource arrives with the ones its author declared. What can go wrong is here rather than at
/// the cluster — a Table is a document, and a document is something a test can hold.
/// </para>
/// </summary>
public sealed class ResourceTableTests
{
    private static readonly GroupVersionKind Certificate = new("cert-manager.io", "v1", "Certificate");

    private static ResourceTable Read(string json, string? fallbackNamespace = null)
    {
        using var document = JsonDocument.Parse(json);
        return ResourceTables.Read(document.RootElement, Certificate, fallbackNamespace);
    }

    [Fact]
    public void The_servers_own_columns_come_through()
    {
        var table = Read("""
        {
          "columnDefinitions": [
            { "name": "Name", "priority": 0 },
            { "name": "Ready", "priority": 0 },
            { "name": "Issuer", "priority": 1 }
          ],
          "rows": []
        }
        """);

        Assert.Equal(["Name", "Ready", "Issuer"], table.Columns.Select(c => c.Name));

        // Priority is kept rather than flattened: 0 is what kubectl prints, higher is what -o wide adds,
        // and the grid holds those back instead of dropping them.
        Assert.Equal([0, 0, 1], table.Columns.Select(c => c.Priority));
    }

    [Fact]
    public void A_row_is_addressable_by_the_metadata_the_server_included()
    {
        var table = Read("""
        {
          "columnDefinitions": [{ "name": "Name", "priority": 0 }],
          "rows": [
            {
              "cells": ["kontena-app-tls"],
              "object": { "metadata": { "name": "kontena-app-tls", "namespace": "system-ingress" } }
            }
          ]
        }
        """);

        var row = Assert.Single(table.Rows);
        Assert.Equal("kontena-app-tls", row.Reference.Name);
        Assert.Equal("system-ingress", row.Reference.Namespace);
        Assert.Equal(Certificate, row.Reference.Kind);
    }

    /// <summary>
    /// A cluster-scoped listing has no namespace on the object, and one asked for inside a namespace
    /// does not repeat it per row. The namespace that was asked for stands in either way.
    /// </summary>
    [Fact]
    public void A_row_without_a_namespace_falls_back_to_the_one_that_was_asked_for()
    {
        var table = Read("""
        {
          "columnDefinitions": [{ "name": "Name", "priority": 0 }],
          "rows": [{ "cells": ["node-1"], "object": { "metadata": { "name": "node-1" } } }]
        }
        """, fallbackNamespace: "argocd");

        Assert.Equal("argocd", Assert.Single(table.Rows).Reference.Namespace);
    }

    /// <summary>
    /// Without a name there is nothing to open or delete, so the row's buttons would act on nothing.
    /// Dropping it beats showing a line that quietly does the wrong thing when clicked.
    /// </summary>
    [Fact]
    public void A_row_the_server_did_not_name_is_left_out()
    {
        var table = Read("""
        {
          "columnDefinitions": [{ "name": "Name", "priority": 0 }],
          "rows": [
            { "cells": ["mystery"] },
            { "cells": ["real"], "object": { "metadata": { "name": "real" } } }
          ]
        }
        """);

        Assert.Equal(["real"], table.Rows.Select(r => r.Reference.Name));
    }

    /// <summary>
    /// Cells are whatever JSON the column's type says. A replica count arriving as <c>3</c> must not
    /// reach the grid as <c>"3"</c> with the quotes, and a null must not read as the word null.
    /// </summary>
    [Fact]
    public void Cells_of_every_json_type_become_plain_text()
    {
        var table = Read("""
        {
          "columnDefinitions": [
            { "name": "Name" }, { "name": "Replicas" }, { "name": "Ready" }, { "name": "Message" }
          ],
          "rows": [
            {
              "cells": ["web", 3, true, null],
              "object": { "metadata": { "name": "web" } }
            }
          ]
        }
        """);

        Assert.Equal(["web", "3", "true", ""], Assert.Single(table.Rows).Cells);
    }

    [Fact]
    public void An_answer_with_nothing_in_it_is_an_empty_table()
    {
        var table = Read("""{ "kind": "Table" }""");

        Assert.Empty(table.Columns);
        Assert.Empty(table.Rows);
    }

    /// <summary>
    /// Where the listing is asked for. Absolute, because the client's <c>HttpClient</c> carries the
    /// credentials and the server certificate but no <c>BaseAddress</c> — a relative path there does not
    /// make a wrong request, it makes no request at all.
    /// </summary>
    [Theory]
    // core, cluster-scoped
    [InlineData("", "v1", "nodes", false, null, "https://10.0.0.2:6443/api/v1/nodes?includeObject=Metadata")]
    // core, namespaced, all namespaces
    [InlineData("", "v1", "configmaps", true, null, "https://10.0.0.2:6443/api/v1/configmaps?includeObject=Metadata")]
    // core, namespaced, one namespace
    [InlineData("", "v1", "configmaps", true, "argocd", "https://10.0.0.2:6443/api/v1/namespaces/argocd/configmaps?includeObject=Metadata")]
    // a group, namespaced
    [InlineData("traefik.io", "v1alpha1", "ingressroutes", true, "system-ingress", "https://10.0.0.2:6443/apis/traefik.io/v1alpha1/namespaces/system-ingress/ingressroutes?includeObject=Metadata")]
    // a namespaced kind asked for across the cluster keeps the namespace segment out
    [InlineData("monitoring.coreos.com", "v1", "servicemonitors", true, null, "https://10.0.0.2:6443/apis/monitoring.coreos.com/v1/servicemonitors?includeObject=Metadata")]
    public void The_listing_is_asked_for_at_an_absolute_address(
        string group, string version, string plural, bool namespaced, string? ns, string expected)
    {
        var uri = ResourceTables.RequestUri(
            new Uri("https://10.0.0.2:6443"), new ApiResourceInfo(group, version, plural, namespaced), ns);

        Assert.True(uri.IsAbsoluteUri);
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    /// <summary>
    /// A base address that already ends in a slash must not gain a second one, and one without must not
    /// have its last segment swallowed by the combine.
    /// </summary>
    [Theory]
    [InlineData("https://10.0.0.2:6443")]
    [InlineData("https://10.0.0.2:6443/")]
    public void A_trailing_slash_on_the_base_address_makes_no_difference(string baseUri)
    {
        var uri = ResourceTables.RequestUri(
            new Uri(baseUri), new ApiResourceInfo(string.Empty, "v1", "pods", true), null);

        Assert.Equal("https://10.0.0.2:6443/api/v1/pods?includeObject=Metadata", uri.AbsoluteUri);
    }

    /// <summary>
    /// Kubernetes reserves <c>k8s.io</c> for its own APIs, so the suffix is what tells "installed by an
    /// operator" from "part of Kubernetes" — which is the heading a kind is listed under.
    /// <para>
    /// The suffix alone is not enough: <c>apps</c>, <c>batch</c>, <c>autoscaling</c> and <c>policy</c>
    /// predate the convention and never got it. Reading those as custom would file Deployments under the
    /// one heading they do not belong to.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("apps", false)]
    [InlineData("batch", false)]
    [InlineData("autoscaling", false)]
    [InlineData("policy", false)]
    [InlineData("networking.k8s.io", false)]
    [InlineData("rbac.authorization.k8s.io", false)]
    [InlineData("cert-manager.io", true)]
    [InlineData("argoproj.io", true)]
    [InlineData("monitoring.coreos.com", true)]
    public void A_group_outside_k8s_io_came_from_somewhere_else(string group, bool custom) =>
        Assert.Equal(custom, ApiResourceResolver.IsCustom(group));
}
