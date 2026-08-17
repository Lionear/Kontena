using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Usage read straight from each kubelet's stats endpoint, through the apiserver proxy
/// (<c>/api/v1/nodes/{node}/proxy/stats/summary</c>) — the same source Lens and k9s use.
/// <para>
/// It earns its place for two reasons. A metrics-server reports <b>no disk at all</b>, so a disk
/// gauge needs this source even on clusters that run one. And it answers on clusters that have no
/// metrics-server, where the alternative is no gauges whatsoever.
/// </para>
/// <para>
/// The trade-offs are real: it needs <c>nodes/proxy</c> RBAC, which ordinary users are often denied
/// on managed clusters, and it costs one request per node where a metrics-server costs one for the
/// whole cluster. Hence <see cref="MaxNodes"/> and a probe that fails quietly into "unavailable".
/// </para>
/// </summary>
internal sealed class KubeletSummarySource(IKubernetes client, Func<CancellationToken, Task<IReadOnlyList<string>>> nodeNames)
    : INodeDiskSource
{
    /// <summary>
    /// Above this many nodes the per-node fan-out costs more than the gauges are worth; the source
    /// reports itself unavailable rather than hammering the apiserver on every refresh.
    /// </summary>
    private const int MaxNodes = 50;

    public string Name => "kubelet";
    public bool IsAvailable { get; private set; }

    public async ValueTask<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var names = await nodeNames(ct).ConfigureAwait(false);
            if (names.Count == 0 || names.Count > MaxNodes)
            {
                IsAvailable = false;
                return false;
            }

            // One real read: proves both that the endpoint exists and that we hold nodes/proxy.
            IsAvailable = await ReadSummaryAsync(names[0], ct).ConfigureAwait(false) is not null;
        }
        catch (Exception)
        {
            IsAvailable = false;
        }

        return IsAvailable;
    }

    public async ValueTask<IReadOnlyDictionary<string, NodeUsage>> GetNodeUsageAsync(CancellationToken ct = default)
    {
        var usage = new Dictionary<string, NodeUsage>(StringComparer.Ordinal);
        if (!IsAvailable)
            return usage;

        foreach (var summary in await ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (summary.Node?.NodeName is not { } name)
                continue;

            usage[name] = new NodeUsage
            {
                CpuMillicores = Millicores(summary.Node.Cpu?.UsageNanoCores),
                MemoryBytes = (long)(summary.Node.Memory?.WorkingSetBytes ?? 0),
                DiskUsedBytes = summary.Node.Fs?.UsedBytes is { } used ? (long)used : null,
            };
        }

        return usage;
    }

    /// <summary>Node filesystem capacity, which the typed node listing cannot supply on its own.</summary>
    public async ValueTask<IReadOnlyDictionary<string, long>> GetNodeDiskCapacityAsync(CancellationToken ct = default)
    {
        var capacity = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!IsAvailable)
            return capacity;

        foreach (var summary in await ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (summary.Node?.NodeName is { } name && summary.Node.Fs?.CapacityBytes is { } total)
                capacity[name] = (long)total;
        }

        return capacity;
    }

    public async ValueTask<IReadOnlyList<Kontena.Sdk.Orchestration.Models.PodMetrics>> GetPodUsageAsync(
        string? ns = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return [];

        var pods = new List<Kontena.Sdk.Orchestration.Models.PodMetrics>();
        foreach (var summary in await ReadAllAsync(ct).ConfigureAwait(false))
        {
            foreach (var pod in summary.Pods ?? [])
            {
                if (pod.PodRef?.Name is not { } name || pod.PodRef.Namespace is not { } podNamespace)
                    continue;
                if (ns is not null && podNamespace != ns)
                    continue;

                pods.Add(new Kontena.Sdk.Orchestration.Models.PodMetrics
                {
                    Pod = name,
                    Namespace = podNamespace,
                    CpuMillicores = Millicores(pod.Cpu?.UsageNanoCores),
                    MemoryBytes = (long)(pod.Memory?.WorkingSetBytes ?? 0),
                    Timestamp = pod.Cpu?.Time ?? DateTimeOffset.UtcNow,
                });
            }
        }

        return pods;
    }

    private readonly Lock _roundLock = new();
    private Task<IReadOnlyList<SummaryPayload>>? _round;

    /// <summary>
    /// Every node's summary, gathered concurrently; nodes that fail are simply absent.
    /// <para>
    /// A round already under way is joined rather than started again (KON-355). A node listing asks
    /// for usage and then for filesystem capacity, and both answers come out of this one payload — so
    /// the fan-out ran twice, back to back, for identical data: on a three-node cluster that is six
    /// per-node requests and two node listings where three and one will do, on every watch event the
    /// open page reloads for. Only an <i>unfinished</i> round is shared, so nothing here can hand back
    /// a summary from before the event that triggered the read; a caller arriving after one completes
    /// starts a fresh one, exactly as before.
    /// </para>
    /// <para>
    /// A joiner inherits the round's cancellation token rather than its own. That is survivable
    /// because <see cref="ReadSummaryAsync"/> already turns any failure into "no numbers from this
    /// node" — a caller whose round is cancelled under it gets a gauge-less refresh, never a throw.
    /// </para>
    /// </summary>
    private Task<IReadOnlyList<SummaryPayload>> ReadAllAsync(CancellationToken ct)
    {
        lock (_roundLock)
        {
            if (_round is { IsCompleted: false } running)
                return running;

            return _round = ReadRoundAsync(ct);
        }
    }

    private async Task<IReadOnlyList<SummaryPayload>> ReadRoundAsync(CancellationToken ct)
    {
        IReadOnlyList<string> names;
        try
        {
            names = await nodeNames(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }

        if (names.Count > MaxNodes)
            return [];

        var summaries = await Task.WhenAll(names.Select(n => ReadSummaryAsync(n, ct))).ConfigureAwait(false);
        return [.. summaries.OfType<SummaryPayload>()];
    }

    private async Task<SummaryPayload?> ReadSummaryAsync(string node, CancellationToken ct)
    {
        try
        {
            using var response = await client.CoreV1
                .ConnectGetNodeProxyWithPathWithHttpMessagesAsync(node, "stats/summary", cancellationToken: ct)
                .ConfigureAwait(false);

            await using var stream = await response.Response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<SummaryPayload>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No nodes/proxy permission, kubelet unreachable, or an unexpected payload — all mean
            // "no numbers from this node", never a broken listing.
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>The kubelet reports CPU in nanocores; Kontena counts milli-cores.</summary>
    private static long Millicores(ulong? nanoCores) =>
        nanoCores is null ? 0 : (long)(nanoCores.Value / 1_000_000m);

    // ── Payload shape (only the fields Kontena reads) ────────────────────────

    private sealed record SummaryPayload
    {
        [JsonPropertyName("node")] public NodeStats? Node { get; init; }
        [JsonPropertyName("pods")] public IReadOnlyList<PodStats>? Pods { get; init; }
    }

    private sealed record NodeStats
    {
        [JsonPropertyName("nodeName")] public string? NodeName { get; init; }
        [JsonPropertyName("cpu")] public CpuStats? Cpu { get; init; }
        [JsonPropertyName("memory")] public MemoryStats? Memory { get; init; }
        [JsonPropertyName("fs")] public FsStats? Fs { get; init; }
    }

    private sealed record PodStats
    {
        [JsonPropertyName("podRef")] public PodRefStats? PodRef { get; init; }
        [JsonPropertyName("cpu")] public CpuStats? Cpu { get; init; }
        [JsonPropertyName("memory")] public MemoryStats? Memory { get; init; }
    }

    private sealed record PodRefStats
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    }

    private sealed record CpuStats
    {
        [JsonPropertyName("time")] public DateTimeOffset? Time { get; init; }
        [JsonPropertyName("usageNanoCores")] public ulong? UsageNanoCores { get; init; }
    }

    private sealed record MemoryStats
    {
        [JsonPropertyName("workingSetBytes")] public ulong? WorkingSetBytes { get; init; }
    }

    private sealed record FsStats
    {
        [JsonPropertyName("usedBytes")] public ulong? UsedBytes { get; init; }
        [JsonPropertyName("capacityBytes")] public ulong? CapacityBytes { get; init; }
    }

    /// <summary>Formats a byte count for diagnostics.</summary>
    internal static string Describe(long bytes) =>
        (bytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
