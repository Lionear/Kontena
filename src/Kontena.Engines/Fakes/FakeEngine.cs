using System.Runtime.CompilerServices;
using Kontena.Core.Errors;
using Kontena.Core.Models;

namespace Kontena.Engines.Fakes;

/// <summary>
/// An in-memory <see cref="IContainerEngine"/> for tests and UI development —
/// no real container engine required. Seeded with sample data that mirrors the
/// mockups; lifecycle operations mutate the in-memory state.
/// </summary>
public sealed class FakeEngine : IContainerEngine
{
    private readonly Dictionary<string, ContainerSummary> _containers = [];
    private readonly Dictionary<string, ImageSummary> _images = [];
    private readonly Dictionary<string, VolumeSummary> _volumes = [];
    private readonly Dictionary<string, NetworkSummary> _networks = [];
    private int _idSeed = 1000;

    public FakeEngine(bool seed = true)
    {
        if (seed) Seed();
    }

    public string Backend => "fake";

    public EngineCapabilities Capabilities { get; } = new()
    {
        Rootless = true,
        SupportsBuild = true,
        SupportsCompose = true,
        SupportsExec = true,
        SupportsPrune = true,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = true,
    };

    public ValueTask<EngineInfo> GetInfoAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(new EngineInfo
        {
            Backend = Backend,
            DisplayName = "Fake engine",
            Version = "0.1.0",
            Endpoint = "memory://",
            ConnectionState = EngineConnectionState.Connected,
        });

    public ValueTask PingAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    // ── Containers ──────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default)
    {
        IReadOnlyList<ContainerSummary> list = _containers.Values
            .Where(c => all || c.State == ContainerState.Running)
            .OrderBy(c => c.Name)
            .ToList();
        return ValueTask.FromResult(list);
    }

    public ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default)
    {
        var id = NextId();
        var summary = new ContainerSummary
        {
            Id = id,
            Name = request.Name ?? $"container-{id[..6]}",
            Image = request.Image,
            State = request.Start ? ContainerState.Running : ContainerState.Created,
            Status = request.Start ? "Up now" : "Created",
            Ports = request.Ports,
            CreatedAt = DateTimeOffset.UtcNow,
            Backend = Backend,
        };
        _containers[id] = summary;
        return ValueTask.FromResult(id);
    }

    public ValueTask StartContainerAsync(string id, CancellationToken ct = default) =>
        Transition(id, ContainerState.Running, "Up now");

    public ValueTask StopContainerAsync(string id, CancellationToken ct = default) =>
        Transition(id, ContainerState.Exited, "Exited (0)");

    public ValueTask RestartContainerAsync(string id, CancellationToken ct = default) =>
        Transition(id, ContainerState.Running, "Up now");

    public ValueTask PauseContainerAsync(string id, CancellationToken ct = default) =>
        Transition(id, ContainerState.Paused, "Paused");

    public ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default) =>
        Transition(id, ContainerState.Running, "Up now");

    public ValueTask RemoveContainerAsync(
        string id, bool force = false, CancellationToken ct = default)
    {
        Require(_containers.Remove(id), $"container {id}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default)
    {
        RequireContainer(id);
        return ValueTask.FromResult(0);
    }

    // ── Images ──────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ImageSummary> list = _images.Values
            .OrderBy(i => i.Repository).ThenBy(i => i.Tag).ToList();
        return ValueTask.FromResult(list);
    }

    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        const long total = 48_000_000;
        for (var step = 1; step <= 4; step++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct).ConfigureAwait(false);
            yield return new PullProgress(reference, $"Downloading layer {step}/4",
                total / 4 * step, total);
        }

        var (repo, tag) = SplitReference(reference);
        var id = NextId();
        _images[id] = new ImageSummary
        {
            Id = id,
            Repository = repo,
            Tag = tag,
            SizeBytes = total,
            CreatedAt = DateTimeOffset.UtcNow,
            InUse = false,
        };
        yield return new PullProgress(reference, "Pull complete", total, total);
    }

    public ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default)
    {
        Require(_images.Remove(id), $"image {id}");
        return ValueTask.CompletedTask;
    }

    public ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default)
    {
        var image = RequireImage(id);
        _images[id] = image with { Tag = newTag };
        return ValueTask.CompletedTask;
    }

    public ValueTask<PruneResult> PruneImagesAsync(bool allUnused = true, CancellationToken ct = default)
    {
        var unused = _images.Where(kv => !kv.Value.InUse).ToList();
        long reclaimed = 0;
        foreach (var kv in unused)
        {
            reclaimed += kv.Value.SizeBytes;
            _images.Remove(kv.Key);
        }

        return ValueTask.FromResult(new PruneResult(unused.Count, reclaimed));
    }

    // ── Volumes ─────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<VolumeSummary> list = _volumes.Values.OrderBy(v => v.Name).ToList();
        return ValueTask.FromResult(list);
    }

    public ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default)
    {
        var volume = new VolumeSummary
        {
            Name = request.Name,
            Driver = request.Driver,
            Mountpoint = $"/var/lib/kontena/volumes/{request.Name}/_data",
        };
        _volumes[request.Name] = volume;
        return ValueTask.FromResult(volume);
    }

    public ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default)
    {
        Require(_volumes.Remove(name), $"volume {name}");
        return ValueTask.CompletedTask;
    }

    // ── Networks ────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NetworkSummary> list = _networks.Values.OrderBy(n => n.Name).ToList();
        return ValueTask.FromResult(list);
    }

    public ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default)
    {
        var id = NextId();
        var network = new NetworkSummary
        {
            Id = id,
            Name = request.Name,
            Driver = request.Driver,
            Subnet = request.Subnet,
        };
        _networks[id] = network;
        return ValueTask.FromResult(network);
    }

    public ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default)
    {
        var network = RequireNetwork(id);
        if (network.IsBuiltIn)
            throw new EngineException($"network {network.Name} is built-in and cannot be removed.");
        _networks.Remove(id);
        return ValueTask.CompletedTask;
    }

    // ── Streams ─────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        RequireContainer(id);
        string[] lines =
        [
            "INFO  starting up",
            "READY listening on 0.0.0.0:80",
            "INFO  GET /api/health 200 2ms",
            "WARN  upstream slow response 812ms",
            "INFO  GET /metrics 200 1ms",
        ];
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
            yield return new LogEntry(DateTimeOffset.UtcNow, LogSource.Stdout, line);
        }
    }

    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        RequireContainer(id);
        for (var i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
            yield return new ContainerStats
            {
                ContainerId = id,
                CpuPercent = 2.0 + i * 0.5,
                MemoryUsedBytes = 128_000_000 + i * 1_000_000,
                MemoryLimitBytes = 512_000_000,
                NetRxBytes = 1_200_000,
                NetTxBytes = 400_000,
                BlockReadBytes = 8_100_000,
                BlockWriteBytes = 2_000_000,
            };
        }
    }

    public async IAsyncEnumerable<EngineEvent> StreamEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var container in _containers.Values.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
            yield return new EngineEvent(EngineEventType.Started, ResourceKind.Container,
                container.Id, DateTimeOffset.UtcNow);
        }

        // Keep the stream open like a real engine would, instead of completing
        // (which would make consumers re-subscribe in a tight loop).
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string NextId() => $"fake{Interlocked.Increment(ref _idSeed):x}00000000";

    private ValueTask Transition(string id, ContainerState state, string status)
    {
        var c = RequireContainer(id);
        _containers[id] = c with { State = state, Status = status };
        return ValueTask.CompletedTask;
    }

    private ContainerSummary RequireContainer(string id) =>
        _containers.TryGetValue(id, out var c)
            ? c
            : throw new ResourceNotFoundException($"container {id} not found.");

    private ImageSummary RequireImage(string id) =>
        _images.TryGetValue(id, out var i)
            ? i
            : throw new ResourceNotFoundException($"image {id} not found.");

    private NetworkSummary RequireNetwork(string id) =>
        _networks.TryGetValue(id, out var n)
            ? n
            : throw new ResourceNotFoundException($"network {id} not found.");

    private static void Require(bool removed, string what)
    {
        if (!removed) throw new ResourceNotFoundException($"{what} not found.");
    }

    private static (string Repository, string Tag) SplitReference(string reference)
    {
        var idx = reference.LastIndexOf(':');
        // Guard against a registry port (host:port/repo) with no tag.
        return idx > 0 && !reference[(idx + 1)..].Contains('/')
            ? (reference[..idx], reference[(idx + 1)..])
            : (reference, "latest");
    }

    private void Seed()
    {
        AddContainer("api-gateway", "nginx:1.27-alpine", ContainerState.Running, "Up 2 hours",
            new PortBinding(8080, 80));
        AddContainer("postgres-main", "postgres:16", ContainerState.Running, "Up 2 hours",
            new PortBinding(5432, 5432));
        AddContainer("redis-cache", "redis:7-alpine", ContainerState.Running, "Up 2 hours",
            new PortBinding(6379, 6379));
        AddContainer("worker-jobs", "ghcr.io/lionear/worker:2.4", ContainerState.Paused, "Paused");
        AddContainer("migrate-db", "flyway/flyway:10", ContainerState.Exited, "Exited (0)");

        AddImage("docker.io/library/nginx", "1.27-alpine", 48_000_000, inUse: true);
        AddImage("docker.io/library/postgres", "16", 438_000_000, inUse: true);
        AddImage("docker.io/library/redis", "7-alpine", 32_000_000, inUse: true);
        AddImage("ghcr.io/lionear/worker", "2.4", 312_000_000, inUse: true);
        AddImage("docker.io/grafana/grafana", "11.2.0", 402_000_000, inUse: false);

        _volumes["pgdata"] = new VolumeSummary
        {
            Name = "pgdata",
            Mountpoint = "/var/lib/kontena/volumes/pgdata/_data",
            SizeBytes = 1_100_000_000,
            UsedBy = ["postgres-main"],
        };
        _volumes["app-uploads"] = new VolumeSummary
        {
            Name = "app-uploads",
            Mountpoint = "/var/lib/kontena/volumes/app-uploads/_data",
            SizeBytes = 148_000_000,
        };

        AddNetwork("kontena_default", "bridge", "172.20.0.0/16", builtIn: false,
            ["api-gateway", "postgres-main", "redis-cache"]);
        AddNetwork("bridge", "bridge", "172.17.0.0/16", builtIn: true, []);
        AddNetwork("host", "host", null, builtIn: true, []);
        AddNetwork("none", "null", null, builtIn: true, []);
    }

    private void AddContainer(
        string name, string image, ContainerState state, string status, params PortBinding[] ports)
    {
        var id = NextId();
        _containers[id] = new ContainerSummary
        {
            Id = id,
            Name = name,
            Image = image,
            State = state,
            Status = status,
            Ports = ports,
            CreatedAt = DateTimeOffset.UtcNow,
            Backend = Backend,
        };
    }

    private void AddImage(string repo, string tag, long size, bool inUse)
    {
        var id = NextId();
        _images[id] = new ImageSummary
        {
            Id = id,
            Repository = repo,
            Tag = tag,
            SizeBytes = size,
            CreatedAt = DateTimeOffset.UtcNow,
            InUse = inUse,
        };
    }

    private void AddNetwork(
        string name, string driver, string? subnet, bool builtIn, IReadOnlyList<string> attached)
    {
        var id = NextId();
        _networks[id] = new NetworkSummary
        {
            Id = id,
            Name = name,
            Driver = driver,
            Subnet = subnet,
            IsBuiltIn = builtIn,
            AttachedContainers = attached,
        };
    }
}
