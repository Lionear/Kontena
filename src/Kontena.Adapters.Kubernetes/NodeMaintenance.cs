using System.Net;
using System.Runtime.CompilerServices;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Cordon, uncordon and drain (KON-251) — the part of node maintenance that <c>kubectl drain</c>
/// does, and the reasons it does it that way.
/// <para>
/// Kept out of <see cref="KubernetesClusterEngine"/> because a drain is a small policy engine, not a
/// call: it decides per pod what may be moved, asks, and waits — and each of those decisions is one
/// somebody will want to read back later.
/// </para>
/// </summary>
internal static class NodeMaintenance
{
    /// <summary>Annotation a kubelet puts on a pod it created from a file on disk.</summary>
    private const string MirrorAnnotation = "kubernetes.io/config.mirror";

    /// <summary>How often to look for a pod to have actually gone.</summary>
    private static readonly TimeSpan GonePoll = TimeSpan.FromSeconds(2);

    public static async Task CordonAsync(
        k8s.Kubernetes client, string node, bool cordoned, CancellationToken ct)
    {
        // A merge patch on the one field: read-modify-write would lose whatever else changed on the
        // node between the read and the write, and this is a field nothing else touches.
        var patch = new V1Patch(new { spec = new { unschedulable = cordoned } }, V1Patch.PatchType.MergePatch);
        await client.CoreV1.PatchNodeAsync(patch, node, cancellationToken: ct).ConfigureAwait(false);
    }

    public static async IAsyncEnumerable<DrainProgress> DrainAsync(
        k8s.Kubernetes client, string node, DrainOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Cordon first, and only then look at the pods. The other order leaves a window in which the
        // scheduler puts new work onto the node you are emptying — which is not a race you can win
        // by being quick, only by closing it.
        await CordonAsync(client, node, cordoned: true, ct).ConfigureAwait(false);
        yield return new DrainProgress { Action = DrainAction.Cordoned };

        var pods = await client.CoreV1
            .ListPodForAllNamespacesAsync(fieldSelector: $"spec.nodeName={node}", cancellationToken: ct)
            .ConfigureAwait(false);

        foreach (var pod in pods.Items ?? [])
        {
            ct.ThrowIfCancellationRequested();

            var name = pod.Metadata?.Name ?? string.Empty;
            var ns = pod.Metadata?.NamespaceProperty ?? "default";

            if (SkipReason(pod, options) is { } skip)
            {
                yield return new DrainProgress
                {
                    Action = DrainAction.Skipped, Pod = name, Namespace = ns, Reason = skip,
                };
                continue;
            }

            yield return new DrainProgress { Action = DrainAction.Evicting, Pod = name, Namespace = ns };

            var outcome = await EvictAsync(client, name, ns, options, ct).ConfigureAwait(false);
            yield return outcome;
        }

        yield return new DrainProgress { Action = DrainAction.Finished };
    }

    /// <summary>
    /// Why this pod is not going anywhere, or null if it should be evicted.
    /// <para>
    /// Each of these is a case where evicting is either impossible or pointless, and saying which is
    /// the difference between a drain that looks incomplete and one that is understood.
    /// </para>
    /// </summary>
    internal static string? SkipReason(V1Pod pod, DrainOptions options)
    {
        // A mirror pod is the kubelet's own copy of a file on that node's disk. The apiserver will
        // accept the eviction and the kubelet will recreate it immediately, because the source of
        // truth is not in the cluster at all.
        if (pod.Metadata?.Annotations?.ContainsKey(MirrorAnnotation) == true)
            return "a static pod — its definition is a file on the node, not an object in the cluster";

        if (pod.Status?.Phase is "Succeeded" or "Failed")
            return "already finished";

        var owner = pod.Metadata?.OwnerReferences?.FirstOrDefault(o => o.Controller == true);

        if (options.IgnoreDaemonSets && owner?.Kind == "DaemonSet")
            return "managed by a DaemonSet, which would put it straight back";

        // emptyDir is scratch space that lives and dies with the pod. Evicting one is not a move, it
        // is a deletion of whatever was in it — so it needs its own yes.
        if (!options.DeleteEmptyDirData && pod.Spec?.Volumes?.Any(v => v.EmptyDir is not null) == true)
            return "uses local scratch storage (emptyDir) that would be lost";

        // A pod with no controller has nothing to recreate it anywhere else. Evicting it is allowed
        // and is what kubectl does with --force, but silently is the wrong way to do it.
        if (owner is null)
            return "not managed by a controller, so nothing would recreate it elsewhere";

        return null;
    }

    private static async Task<DrainProgress> EvictAsync(
        k8s.Kubernetes client, string name, string ns, DrainOptions options, CancellationToken ct)
    {
        var eviction = new V1Eviction
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
        };

        try
        {
            await client.CoreV1.CreateNamespacedPodEvictionAsync(eviction, name, ns, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException failure)
            when (failure.Response?.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 429 from the eviction endpoint is not rate limiting: it is a PodDisruptionBudget saying
            // that letting this pod go would take the workload below what it promises to keep. The
            // apiserver's message names the budget, so it is passed through rather than summarised.
            return new DrainProgress
            {
                Action = DrainAction.Blocked,
                Pod = name,
                Namespace = ns,
                Reason = MessageOf(failure) ?? "a PodDisruptionBudget will not allow it right now",
            };
        }
        catch (HttpOperationException failure) when (failure.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // It went while we were asking. That is the outcome we wanted.
            return new DrainProgress { Action = DrainAction.Evicted, Pod = name, Namespace = ns };
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return new DrainProgress
            {
                Action = DrainAction.Failed, Pod = name, Namespace = ns, Reason = failure.Message,
            };
        }

        return await WaitForGoneAsync(client, name, ns, options.Timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the pod to actually be gone.
    /// <para>
    /// Accepting the eviction is not the same as the pod having left: a graceful shutdown takes as
    /// long as its termination grace period, and a pod that refuses to stop keeps its place on the
    /// node the whole time. Reporting "evicted" at the moment of asking would call a drain finished
    /// while the node is still busy.
    /// </para>
    /// </summary>
    private static async Task<DrainProgress> WaitForGoneAsync(
        k8s.Kubernetes client, string name, string ns, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (HttpOperationException failure) when (failure.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return new DrainProgress { Action = DrainAction.Evicted, Pod = name, Namespace = ns };
            }

            await Task.Delay(GonePoll, ct).ConfigureAwait(false);
        }

        return new DrainProgress
        {
            Action = DrainAction.Failed,
            Pod = name,
            Namespace = ns,
            Reason = $"still there {timeout.TotalMinutes:0} minutes after being asked to go",
        };
    }

    /// <summary>The apiserver's own message, which is where the budget's name is.</summary>
    private static string? MessageOf(HttpOperationException failure)
    {
        var body = failure.Response?.Content;
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return KubernetesJson.Deserialize<V1Status>(body)?.Message;
        }
        catch (Exception)
        {
            // A body that is not a Status is not worth failing over; the generic wording covers it.
            return null;
        }
    }
}
