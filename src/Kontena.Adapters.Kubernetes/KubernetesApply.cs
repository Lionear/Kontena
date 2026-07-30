using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// The declarative core against a real API server (KON-86): server-side apply, with
/// <c>dryRun=All</c> for the preview.
/// <para>
/// Server-side apply is what makes the preview trustworthy. The request goes through the real
/// admission chain — defaulting, validating webhooks, quota, policy — and the server returns the
/// object as it <i>would</i> end up, without persisting anything. Diffing that against the live
/// object is therefore an answer from the cluster, not a guess Kontena made locally.
/// </para>
/// <para>
/// Everything is addressed generically through the custom-objects endpoint with a discovered
/// plural, so custom resources work on the same path as built-in kinds, with no special cases.
/// </para>
/// </summary>
internal sealed class KubernetesApply(IKubernetes client, ApiResourceResolver resolver)
{
    /// <summary>
    /// Field manager recorded on every field this application owns. Kubernetes uses it to detect
    /// conflicts between actors, so it must be stable and identifiably ours.
    /// </summary>
    private const string FieldManager = "kontena";

    /// <summary>Apply (or preview) one decoded document.</summary>
    /// <param name="pendingNamespaces">
    /// Namespaces this same bundle creates. During a dry-run they do not exist yet, so resources
    /// targeting them are rejected; knowing which they are turns a baffling error into an explanation.
    /// </param>
    public async Task<ApplyProgress> ApplyOneAsync(
        Dictionary<string, object?> document, bool dryRun, string? defaultNamespace,
        IReadOnlySet<string> pendingNamespaces, CancellationToken ct)
    {
        if (!TryReadIdentity(document, out var gvk, out var name, out var ns, out var error))
            return Failed(new ResourceRef(gvk, ns, name), error!);

        ns ??= defaultNamespace;
        var reference = new ResourceRef(gvk, ns, name);

        var resource = await resolver.ResolveAsync(gvk, ct).ConfigureAwait(false);
        if (resource is null)
        {
            return Failed(reference,
                $"The cluster does not serve {gvk.Kind} ({(gvk.IsCoreGroup ? gvk.Version : $"{gvk.Group}/{gvk.Version}")}). " +
                "A missing CRD is the usual cause.");
        }

        if (resource.Namespaced && string.IsNullOrEmpty(ns))
            return Failed(reference, $"{gvk.Kind}/{name} is namespaced but no namespace was given.");

        if (!resource.Namespaced)
            reference = new ResourceRef(gvk, null, name);

        var live = await ReadAsync(resource, ns, name, ct).ConfigureAwait(false);

        try
        {
            var result = await PatchAsync(document, resource, ns, name, dryRun, ct).ConfigureAwait(false);

            if (live is null)
            {
                return new ApplyProgress
                {
                    Resource = reference,
                    Action = dryRun ? ApplyAction.WouldCreate : ApplyAction.Created,
                    Diff = ManifestDiff.Compute(string.Empty, ManifestNormalizer.ToComparableYaml(result)),
                };
            }

            var diff = ManifestDiff.Compute(
                ManifestNormalizer.ToComparableYaml(live),
                ManifestNormalizer.ToComparableYaml(result));

            if (diff.Length == 0)
                return new ApplyProgress { Resource = reference, Action = ApplyAction.Unchanged };

            return new ApplyProgress
            {
                Resource = reference,
                Action = dryRun ? ApplyAction.WouldChange : ApplyAction.Configured,
                Diff = diff,
            };
        }
        catch (HttpOperationException ex)
        {
            // Rejections are the point of a server-side dry-run: admission webhooks, immutable
            // fields, quota and RBAC all surface here, and the message is the useful part.
            var message = Describe(ex);

            // One rejection is an artefact rather than a problem with the manifest: a dry-run cannot
            // place a resource in a namespace the same bundle would create, because nothing was
            // persisted. kubectl behaves identically; say so instead of echoing "not found".
            if (dryRun && ns is not null && pendingNamespaces.Contains(ns) &&
                message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                message =
                    $"Cannot preview this resource: namespace \"{ns}\" does not exist yet — this bundle " +
                    "creates it. Apply the namespace first, or apply the bundle for real to validate it.";
            }

            return Failed(reference, message);
        }
        catch (Exception ex)
        {
            return Failed(reference, ex.Message);
        }
    }

    /// <summary>Delete a resource, resolving its plural the same way apply does.</summary>
    public async Task DeleteAsync(ResourceRef reference, bool force, CancellationToken ct)
    {
        var resource = await resolver.ResolveAsync(reference.Kind, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException(
                           $"The cluster does not serve {reference.Kind.Kind}.");

        var options = force ? new V1DeleteOptions { GracePeriodSeconds = 0 } : new V1DeleteOptions();

        if (resource.Namespaced)
        {
            await client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                resource.Group, resource.Version, reference.Namespace, resource.Plural, reference.Name,
                body: options, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            await client.CustomObjects.DeleteClusterCustomObjectAsync(
                resource.Group, resource.Version, resource.Plural, reference.Name,
                body: options, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Patch a workload's <c>/scale</c> subresource.</summary>
    public async Task ScaleAsync(ResourceRef workload, int replicas, CancellationToken ct)
    {
        var resource = await resolver.ResolveAsync(workload.Kind, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"The cluster does not serve {workload.Kind.Kind}.");

        var patch = new V1Patch(
            new { spec = new { replicas } }, V1Patch.PatchType.MergePatch);

        await client.CustomObjects.PatchNamespacedCustomObjectScaleAsync(
            patch, resource.Group, resource.Version, workload.Namespace, resource.Plural, workload.Name,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Trigger a rolling restart the way <c>kubectl rollout restart</c> does — stamp the pod
    /// template with a fresh annotation so the controller rolls out a new revision.
    /// </summary>
    public async Task RolloutRestartAsync(ResourceRef workload, DateTimeOffset now, CancellationToken ct)
    {
        var resource = await resolver.ResolveAsync(workload.Kind, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"The cluster does not serve {workload.Kind.Kind}.");

        var patch = new V1Patch(
            new
            {
                spec = new
                {
                    template = new
                    {
                        metadata = new
                        {
                            annotations = new Dictionary<string, string>
                            {
                                ["kubectl.kubernetes.io/restartedAt"] = now.UtcDateTime.ToString("o"),
                            },
                        },
                    },
                },
            },
            V1Patch.PatchType.MergePatch);

        await client.CustomObjects.PatchNamespacedCustomObjectAsync(
            patch, resource.Group, resource.Version, workload.Namespace, resource.Plural, workload.Name,
            cancellationToken: ct).ConfigureAwait(false);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task<object?> PatchAsync(
        Dictionary<string, object?> document, ApiResourceInfo resource, string? ns, string name,
        bool dryRun, CancellationToken ct)
    {
        // The body goes out as JSON, which the server accepts for apply-patch (JSON is valid YAML).
        var patch = new V1Patch(document, V1Patch.PatchType.ApplyPatch);
        var dry = dryRun ? "All" : null;

        // force: this application takes ownership of the fields it sets. Without it, a field another
        // manager owns makes the whole apply fail with a conflict.
        return resource.Namespaced
            ? await client.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch, resource.Group, resource.Version, ns, resource.Plural, name,
                dryRun: dry, fieldManager: FieldManager, force: true, cancellationToken: ct).ConfigureAwait(false)
            : await client.CustomObjects.PatchClusterCustomObjectAsync(
                patch, resource.Group, resource.Version, resource.Plural, name,
                dryRun: dry, fieldManager: FieldManager, force: true, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>The live object, or null when it does not exist yet (which makes the apply a create).</summary>
    private async Task<object?> ReadAsync(ApiResourceInfo resource, string? ns, string name, CancellationToken ct)
    {
        try
        {
            return resource.Namespaced
                ? await client.CustomObjects.GetNamespacedCustomObjectAsync(
                    resource.Group, resource.Version, ns, resource.Plural, name, ct).ConfigureAwait(false)
                : await client.CustomObjects.GetClusterCustomObjectAsync(
                    resource.Group, resource.Version, resource.Plural, name, ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Pull the identity out of a decoded document, reporting what is missing.</summary>
    private static bool TryReadIdentity(
        Dictionary<string, object?> document,
        out GroupVersionKind gvk, out string name, out string? ns, out string? error)
    {
        gvk = default;
        name = "?";
        ns = null;

        var apiVersion = Text(document, "apiVersion");
        var kind = Text(document, "kind");
        if (string.IsNullOrEmpty(kind))
        {
            error = "Invalid manifest: missing 'kind'.";
            return false;
        }

        if (string.IsNullOrEmpty(apiVersion))
        {
            error = "Invalid manifest: missing 'apiVersion'.";
            return false;
        }

        var slash = apiVersion.LastIndexOf('/');
        gvk = slash < 0
            ? new GroupVersionKind(string.Empty, apiVersion, kind)
            : new GroupVersionKind(apiVersion[..slash], apiVersion[(slash + 1)..], kind);

        if (document.TryGetValue("metadata", out var value) && value is IDictionary<string, object?> metadata)
        {
            name = Text(metadata, "name") ?? "?";
            ns = Text(metadata, "namespace");
        }

        if (name == "?")
        {
            error = "Invalid manifest: missing 'metadata.name'.";
            return false;
        }

        error = null;
        return true;
    }

    private static string? Text(IDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>
    /// Turn an API error into something worth reading. The server puts the useful sentence in the
    /// Status body — the HTTP status alone rarely explains a rejected apply.
    /// </summary>
    private static string Describe(HttpOperationException ex)
    {
        var body = ex.Response?.Content;
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var status = KubernetesJson.Deserialize<V1Status>(body);
                if (!string.IsNullOrEmpty(status?.Message))
                    return status.Message;
            }
            catch (Exception)
            {
                // Not a Status body — fall through to the raw message.
            }
        }

        return ex.Response?.StatusCode == HttpStatusCode.Forbidden
            ? "Forbidden — the current credentials may not apply this resource."
            : ex.Message;
    }

    private static ApplyProgress Failed(ResourceRef reference, string error) =>
        new() { Resource = reference, Action = ApplyAction.Failed, Error = error };
}
