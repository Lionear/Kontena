using System.Net;
using System.Net.Http;
using System.Text.Json;
using Kontena.Adapters.Kubernetes;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// Reading one object of a kind nobody modelled, and following any kind (KON-483). The documents below
/// are shaped after CloudNativePG's <c>Cluster</c> — the kind the ticket was opened over — so what the
/// detail page gets out of them is what it gets out of a real one.
/// </summary>
public sealed class CustomResourceObjectTests
{
    private static readonly GroupVersionKind Cluster = new("postgresql.cnpg.io", "v1", "Cluster");
    private static readonly ResourceRef UmcCluster = new(Cluster, "databases", "umc-cluster");
    private static readonly Uri Server = new("https://10.0.0.1:6443/");
    private static readonly ApiResourceInfo Clusters = new("postgresql.cnpg.io", "v1", "clusters", Namespaced: true);

    private const string OneCluster = """
    {
      "kind": "Table",
      "columnDefinitions": [
        { "name": "Name", "priority": 0 },
        { "name": "Age", "priority": 0 },
        { "name": "Instances", "priority": 0 },
        { "name": "Ready", "priority": 0 },
        { "name": "Status", "priority": 0 },
        { "name": "Primary", "priority": 0 }
      ],
      "rows": [
        {
          "cells": ["umc-cluster", "3d", 2, 2, "Cluster in healthy state", "umc-cluster-1"],
          "object": {
            "apiVersion": "postgresql.cnpg.io/v1",
            "kind": "Cluster",
            "metadata": {
              "name": "umc-cluster",
              "namespace": "databases",
              "creationTimestamp": "2026-09-28T10:00:00Z",
              "labels": { "app": "umc" },
              "annotations": { "cnpg.io/reloadedAt": "2026-09-30T08:00:00Z" },
              "ownerReferences": [
                { "apiVersion": "umc.example.com/v1alpha1", "kind": "Database", "name": "umc", "controller": true }
              ]
            },
            "status": {
              "instances": 2,
              "readyInstances": 2,
              "phase": "Cluster in healthy state",
              "currentPrimary": "umc-cluster-1",
              "instancesStatus": { "healthy": ["umc-cluster-1", "umc-cluster-2"] },
              "conditions": [
                { "type": "Ready", "status": "True", "reason": "ClusterIsReady", "message": "Cluster is Ready", "lastTransitionTime": "2026-09-30T08:01:00Z" },
                { "type": "ContinuousArchiving", "status": "False", "reason": "ContinuousArchivingFailing", "message": "" }
              ]
            }
          }
        }
      ]
    }
    """;

    private static ResourceObject? TryRead(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ResourceTables.ReadObject(document.RootElement, UmcCluster);
    }

    private static ResourceObject Read(string json) =>
        TryRead(json) ?? throw new InvalidOperationException("The table held no object.");

    [Fact]
    public void The_printer_columns_come_through_without_the_name()
    {
        var o = Read(OneCluster);

        Assert.Equal(["Age", "Instances", "Ready", "Status", "Primary"], o.Columns.Select(c => c.Name));
        Assert.Equal(["3d", "2", "2", "Cluster in healthy state", "umc-cluster-1"], o.Columns.Select(c => c.Value));
    }

    [Fact]
    public void The_scalar_status_fields_come_through_and_the_nested_ones_stay_in_the_yaml()
    {
        var o = Read(OneCluster);

        Assert.Equal(["instances", "readyInstances", "phase", "currentPrimary"], o.Status.Select(s => s.Name));
        Assert.DoesNotContain(o.Status, s => s.Name is "instancesStatus" or "conditions");
    }

    [Fact]
    public void Conditions_are_read_by_the_convention_every_operator_follows()
    {
        var o = Read(OneCluster);

        Assert.Equal(["Ready", "ContinuousArchiving"], o.Conditions.Select(c => c.Type));
        Assert.Equal("True", o.Conditions[0].Status);
        Assert.Equal("Cluster is Ready", o.Conditions[0].Message);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 8, 1, 0, TimeSpan.Zero), o.Conditions[0].LastTransition);
        Assert.Null(o.Conditions[1].LastTransition);
    }

    [Fact]
    public void Metadata_and_owners_come_through_as_references_a_page_can_open()
    {
        var o = Read(OneCluster);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), o.Created);
        Assert.Equal("umc", o.Labels["app"]);
        Assert.True(o.Annotations.ContainsKey("cnpg.io/reloadedAt"));

        var owner = Assert.Single(o.Owners);
        Assert.Equal(new GroupVersionKind("umc.example.com", "v1alpha1", "Database"), owner.Kind);
        Assert.Equal("databases", owner.Namespace);
        Assert.Equal("umc", owner.Name);
    }

    [Theory]
    [InlineData("v1", "", "v1")]
    [InlineData("apps/v1", "apps", "v1")]
    [InlineData("postgresql.cnpg.io/v1", "postgresql.cnpg.io", "v1")]
    public void An_owners_api_version_is_split_into_group_and_version(string apiVersion, string group, string version)
    {
        var kind = ResourceTables.KindOf(apiVersion, "Thing");

        Assert.Equal(group, kind.Group);
        Assert.Equal(version, kind.Version);
    }

    [Fact]
    public void A_table_with_no_row_is_no_object()
    {
        Assert.Null(TryRead("""{ "columnDefinitions": [], "rows": [] }"""));
    }

    /// <summary>Answers with a fixed body and status, and remembers what was asked.</summary>
    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task One_object_is_asked_for_as_a_table_with_the_whole_object_included()
    {
        var server = new Answer(HttpStatusCode.OK, OneCluster);
        using var http = new HttpClient(server);

        var o = await ResourceTables.GetAsync(http, Server, Clusters, UmcCluster, default);

        Assert.NotNull(o);
        Assert.Equal(
            "https://10.0.0.1:6443/apis/postgresql.cnpg.io/v1/namespaces/databases/clusters/umc-cluster?includeObject=Object",
            server.Asked?.RequestUri?.AbsoluteUri);
        Assert.Contains("as=Table", server.Asked?.Headers.Accept.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_object_that_is_not_there_is_null_and_a_refusal_is_an_error()
    {
        using var missing = new HttpClient(new Answer(HttpStatusCode.NotFound, "{}"));
        Assert.Null(await ResourceTables.GetAsync(missing, Server, Clusters, UmcCluster, default));

        // A 403 is not an absent object, and the page has to be able to say which it was.
        using var refused = new HttpClient(new Answer(HttpStatusCode.Forbidden, "{}"));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => ResourceTables.GetAsync(refused, Server, Clusters, UmcCluster, default));
    }

    // ── Watch ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ADDED", WatchEventType.Added)]
    [InlineData("MODIFIED", WatchEventType.Modified)]
    [InlineData("DELETED", WatchEventType.Deleted)]
    public void A_watch_line_names_the_object_that_moved(string type, WatchEventType expected)
    {
        var e = ResourceWatch.Read(
            """{"type":""" + $"\"{type}\"" + ""","object":{"kind":"PartialObjectMetadata","metadata":{"name":"umc-cluster","namespace":"databases"}}}""",
            Cluster, null);

        Assert.Equal(expected, e?.Type);
        Assert.Equal(UmcCluster, e?.Resource);
    }

    [Theory]
    [InlineData("""{"type":"BOOKMARK","object":{"metadata":{"resourceVersion":"12"}}}""")]
    [InlineData("""{"type":"ERROR","object":{"kind":"Status","code":410}}""")]
    [InlineData("")]
    public void A_line_that_is_not_an_object_event_is_skipped(string line)
    {
        Assert.Null(ResourceWatch.Read(line, Cluster, null));
    }

    [Fact]
    public async Task Any_kind_is_watched_on_its_own_path_asking_for_metadata_only()
    {
        var server = new Answer(HttpStatusCode.OK, string.Join('\n',
            """{"type":"ADDED","object":{"metadata":{"name":"umc-cluster","namespace":"databases"}}}""",
            """{"type":"MODIFIED","object":{"metadata":{"name":"umc-cluster","namespace":"databases"}}}""",
            """{"type":"ERROR","object":{"kind":"Status","code":410}}""",
            """{"type":"MODIFIED","object":{"metadata":{"name":"never-read","namespace":"databases"}}}"""));
        using var http = new HttpClient(server);

        var events = new List<ResourceEvent>();
        await foreach (var e in ResourceWatch.WatchAsync(http, Server, Clusters, Cluster, "databases", default))
            events.Add(e);

        Assert.Equal(
            "https://10.0.0.1:6443/apis/postgresql.cnpg.io/v1/namespaces/databases/clusters?watch=1",
            server.Asked?.RequestUri?.AbsoluteUri);
        Assert.Contains("as=PartialObjectMetadata", server.Asked?.Headers.Accept.ToString() ?? "", StringComparison.Ordinal);

        // The ERROR ends the stream: what follows it is never read.
        Assert.Equal([WatchEventType.Added, WatchEventType.Modified], events.Select(e => e.Type));
    }

    [Fact]
    public async Task A_refused_watch_is_an_empty_stream()
    {
        using var http = new HttpClient(new Answer(HttpStatusCode.Forbidden, "{}"));

        var events = new List<ResourceEvent>();
        await foreach (var e in ResourceWatch.WatchAsync(http, Server, Clusters, Cluster, null, default))
            events.Add(e);

        Assert.Empty(events);
    }
}
