using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk;

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
    private readonly List<CreateContainerRequest> _createdRequests = [];
    private int _idSeed = 1000;
    private readonly string _backend;
    private readonly string _displayName;

    // ponytail: one shared channel, so two simultaneous StreamEventsAsync readers on one engine would
    // split pushed events between them (a Channel<T> is a queue, not pub/sub). No test needs that yet.
    private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>();

    /// <summary>Test hook (KON-308): push one more event into any event stream already open, after
    /// its initial snapshot — the container-side twin of FakeClusterEngine.EmitWatchEvent.</summary>
    public void EmitEvent(EngineEvent ev) => _events.Writer.TryWrite(ev);

    public FakeEngine(bool seed = true, string backend = "fake", string displayName = "Fake engine")
    {
        _backend = backend;
        _displayName = displayName;
        if (seed) Seed();
    }

    public string Backend => _backend;

    /// <summary>
    /// Everything on, so a caller sees the full UI by default. Settable because "what does this screen
    /// do against an engine that cannot do X" is a question only a fake can answer cheaply — the real
    /// adapters each hard-code their own answer.
    /// </summary>
    public EngineCapabilities Capabilities { get; init; } = new()
    {
        Rootless = true,
        SupportsBuild = true,
        SupportsCompose = true,
        SupportsExec = true,
        SupportsRestartPolicy = true,
        SupportsPrune = true,
        SupportsVolumeBrowse = true,
        SupportsVolumeTransfer = true,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = true,
    };

    public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(new BackendInfo
        {
            Backend = Backend,
            DisplayName = _displayName,
            Kind = "container engine",
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

    /// <summary>Every request this fake was asked to create, in order. Read by migration tests.</summary>
    public IReadOnlyList<CreateContainerRequest> CreatedRequests => _createdRequests;

    public ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default)
    {
        _createdRequests.Add(request);

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

    public ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        var c = RequireContainer(id);
        var running = c.State == ContainerState.Running;

        return ValueTask.FromResult(new ContainerInspect
        {
            Id = c.Id,
            Name = c.Name,
            Image = c.Image,
            ImageId = $"sha256:{c.Id}",
            State = c.State,
            Status = c.State.ToString().ToLowerInvariant(),
            CreatedAt = c.CreatedAt,
            StartedAt = running ? c.CreatedAt : null,
            ExitCode = 0,
            Pid = running ? 4242 : 0,
            RestartPolicy = RestartPolicy.UnlessStopped,
            Command = "/docker-entrypoint.sh nginx -g 'daemon off;'",
            WorkingDirectory = "/",
            User = string.Empty,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                ["NGINX_VERSION"] = "1.27.1",
            },
            Labels = new Dictionary<string, string>
            {
                ["maintainer"] = "NGINX Docker Maintainers",
                ["com.kontena.demo"] = "true",
            },
            Mounts =
            [
                new InspectMount("volume", $"{c.Name}-data", "/var/lib/data", ReadWrite: true),
            ],
            Networks =
            [
                new InspectNetwork("bridge", running ? "172.17.0.2" : string.Empty, "172.17.0.1"),
            ],
        });
    }

    public ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default)
    {
        RequireContainer(id);
        return ValueTask.FromResult<IExecSession>(new FakeExecSession());
    }

    public ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default)
    {
        var stopped = _containers
            .Where(kv => kv.Value.State is ContainerState.Exited or ContainerState.Created or ContainerState.Dead)
            .ToList();
        foreach (var kv in stopped)
            _containers.Remove(kv.Key);

        return ValueTask.FromResult(new PruneResult(stopped.Count, 0));
    }

    // ── Images ──────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ImageSummary> list = _images.Values
            .OrderBy(i => i.Repository).ThenBy(i => i.Tag).ToList();
        return ValueTask.FromResult(list);
    }

    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null,
        [EnumeratorCancellation] CancellationToken ct = default)
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

    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string[] lines =
        [
            "Step 1/5 : FROM node:22-slim AS build",
            " ---> Using cache",
            "Step 2/5 : WORKDIR /app",
            " ---> Using cache",
            "Step 3/5 : COPY package*.json ./",
            " ---> Using cache",
            "Step 4/5 : RUN npm ci --omit=dev",
            "added 214 packages in 17s",
            "Step 5/5 : COPY . . && npm run build",
            "creating an optimized production build…",
            "Successfully built fake0build01",
            $"Successfully tagged {request.Tag}",
        ];

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2, ct).ConfigureAwait(false);
            yield return new BuildProgress(line);
        }

        var (repo, tag) = SplitReference(request.Tag);
        var id = NextId();
        _images[id] = new ImageSummary
        {
            Id = id,
            Repository = repo,
            Tag = tag,
            SizeBytes = 96_000_000,
            CreatedAt = DateTimeOffset.UtcNow,
            InUse = false,
        };
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

    public ValueTask<ImageConfig?> InspectImageAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return ValueTask.FromResult<ImageConfig?>(null);

        // Pretend every known-ish image exposes a port and declares a data volume,
        // so the Run modal's pre-fill can be exercised without a real engine.
        return ValueTask.FromResult<ImageConfig?>(new ImageConfig
        {
            ExposedPorts = [new PortBinding(null, 5432, "tcp")],
            Volumes = ["/var/lib/data"],
            Environment = new Dictionary<string, string> { ["APP_ENV"] = "production" },
        });
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

    /// <summary>
    /// A small, fixed tree per volume — enough to drive the browser UI without an engine. Unknown
    /// directories come back empty rather than throwing: an empty directory is an ordinary thing to
    /// open, and the seed does not pretend to model a filesystem.
    /// </summary>
    /// <summary>
    /// Accepts any login with both fields filled, and refuses the rest — enough to exercise both paths in
    /// the UI without a registry to talk to.
    /// </summary>
    public ValueTask VerifyRegistryLoginAsync(RegistryCredential credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrWhiteSpace(credential.Secret))
            throw new InvalidOperationException("A username and a password or token are both required.");

        return ValueTask.CompletedTask;
    }

    public ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default)
    {
        var normalized = "/" + (path ?? "/").Trim('/');
        IReadOnlyList<VolumeEntry> entries = normalized switch
        {
            "/" =>
            [
                new VolumeEntry("data", true, 0, DateTimeOffset.UtcNow.AddDays(-3)),
                new VolumeEntry("logs", true, 0, DateTimeOffset.UtcNow.AddHours(-2)),
                new VolumeEntry("postgresql.conf", false, 28_431, DateTimeOffset.UtcNow.AddDays(-3)),
                new VolumeEntry("PG_VERSION", false, 3, DateTimeOffset.UtcNow.AddDays(-30)),
            ],
            "/data" =>
            [
                new VolumeEntry("base", true, 0, DateTimeOffset.UtcNow.AddDays(-3)),
                new VolumeEntry("global", true, 0, DateTimeOffset.UtcNow.AddDays(-3)),
                new VolumeEntry("pg_wal", true, 0, DateTimeOffset.UtcNow.AddMinutes(-4)),
            ],
            "/logs" =>
            [
                new VolumeEntry("postgresql-2026-07-26.log", false, 1_284_112, DateTimeOffset.UtcNow.AddMinutes(-1)),
                new VolumeEntry("postgresql-2026-07-25.log", false, 8_912_004, DateTimeOffset.UtcNow.AddDays(-1)),
            ],
            _ => [],
        };

        return ValueTask.FromResult(new VolumeListing(normalized, entries, Truncated: false));
    }

    /// <summary>Volume name → its contents, as the migration tests stand them up and read them back.</summary>
    public IDictionary<string, byte[]> VolumeContents { get; } =
        new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>
    /// Writes a real file, so a caller's staging, ordering and cleanup are exercised for real instead
    /// of against a method that only remembers it was called.
    /// </summary>
    public async ValueTask ExportVolumeAsync(
        string name, string archivePath, CancellationToken ct = default) =>
        await File.WriteAllBytesAsync(
            archivePath,
            VolumeContents.TryGetValue(name, out var content) ? content : [],
            ct).ConfigureAwait(false);

    /// <inheritdoc cref="ExportVolumeAsync"/>
    public async ValueTask ImportVolumeAsync(
        string name, string archivePath, CancellationToken ct = default) =>
        VolumeContents[name] = await File.ReadAllBytesAsync(archivePath, ct).ConfigureAwait(false);

    public ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default)
    {
        Require(_volumes.Remove(name), $"volume {name}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default)
    {
        var dangling = _volumes.Where(kv => kv.Value.IsDangling).ToList();
        long reclaimed = 0;
        foreach (var kv in dangling)
        {
            reclaimed += kv.Value.SizeBytes ?? 0;
            _volumes.Remove(kv.Key);
        }

        return ValueTask.FromResult(new PruneResult(dangling.Count, reclaimed));
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

    /// <summary>
    /// Records the attachment on the network so the list reflects it, which is what the UI reads back.
    /// Names, not ids: that is what <see cref="NetworkSummary.AttachedContainers"/> carries.
    /// </summary>
    public ValueTask ConnectNetworkAsync(string containerId, string networkId, CancellationToken ct = default)
    {
        if (!_networks.TryGetValue(networkId, out var network))
            throw new InvalidOperationException($"No such network: {networkId}");

        var name = NameOf(containerId);
        if (!network.AttachedContainers.Contains(name, StringComparer.Ordinal))
        {
            _networks[networkId] = network with
            {
                AttachedContainers = [.. network.AttachedContainers, name],
            };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default)
    {
        if (!_networks.TryGetValue(networkId, out var network))
            throw new InvalidOperationException($"No such network: {networkId}");

        var name = NameOf(containerId);
        _networks[networkId] = network with
        {
            AttachedContainers = [.. network.AttachedContainers.Where(c => !string.Equals(c, name, StringComparison.Ordinal))],
        };

        return ValueTask.CompletedTask;
    }

    /// <summary>The container's name, or the id when nothing is seeded under it.</summary>
    private string NameOf(string containerId) =>
        _containers.TryGetValue(containerId, out var container) ? container.Name : containerId;

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
        // (which would make consumers re-subscribe in a tight loop) — reading whatever a test pushes
        // via EmitEvent. ReadAllAsync throws OperationCanceledException on ct, which is the same
        // "open until you stop listening" behaviour the infinite delay had.
        await foreach (var ev in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    public async IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var project = string.IsNullOrWhiteSpace(request.ProjectName)
            ? DeriveProjectName(request.ComposeFilePath)
            : request.ProjectName!;
        var fileName = Path.GetFileName(request.ComposeFilePath);

        yield return new ComposeProgress($"parsing {fileName}");
        await Task.Delay(120, ct).ConfigureAwait(false);

        var netName = $"{project}_default";
        yield return new ComposeProgress($"Network {netName}  Created");
        AddNetwork(netName, "bridge", "172.30.0.0/16", builtIn: false, []);

        (string Service, string Image, PortBinding Port)[] services =
        [
            ("web", "nginx:1.27-alpine", new PortBinding(8080, 80)),
            ("api", "ghcr.io/demo/api:latest", new PortBinding(3000, 3000)),
            ("db", "postgres:16", new PortBinding(5432, 5432)),
        ];

        foreach (var (service, image, port) in services)
        {
            ct.ThrowIfCancellationRequested();
            var containerName = $"{project}-{service}-1";
            yield return new ComposeProgress($"Container {containerName}  Creating");
            await Task.Delay(90, ct).ConfigureAwait(false);
            AddComposeContainer(project, service, request.ComposeFilePath, image,
                ContainerState.Running, "Up now", port);
            yield return new ComposeProgress($"Container {containerName}  Started");
        }

        yield return new ComposeProgress($"Project \"{project}\" is up — {services.Length} services running.");
    }

    private static string DeriveProjectName(string composeFilePath)
    {
        var dir = Path.GetDirectoryName(composeFilePath);
        var name = string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "project" : name.ToLowerInvariant();
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

        // Compose makes a <project>_default per stack, and a Down removes it along with the
        // containers. Without these the fake looks like a machine where taking a project down costs
        // nothing but containers, which is not what happens (KON-162).
        AddNetwork("ashenmoon-stack_default", "bridge", "172.21.0.0/16", builtIn: false,
            ["ashenmoon-stack-gateway-1", "ashenmoon-stack-api-1", "ashenmoon-stack-db-1",
             "ashenmoon-stack-redis-1"]);
        AddNetwork("monitoring_default", "bridge", "172.22.0.0/16", builtIn: false,
            ["monitoring-prometheus-1", "monitoring-grafana-1"]);

        AddNetwork("bridge", "bridge", "172.17.0.0/16", builtIn: true, []);
        AddNetwork("host", "host", null, builtIn: true, []);
        AddNetwork("none", "null", null, builtIn: true, []);

        // Two Compose projects (discovered from labels) for the Projects page.
        const string stack = "~/dev/ashenmoon/docker-compose.yml";
        AddComposeContainer("ashenmoon-stack", "gateway", stack, "nginx:1.27-alpine",
            ContainerState.Running, "Up 2 hours", new PortBinding(8080, 80));
        AddComposeContainer("ashenmoon-stack", "api", stack, "ghcr.io/lionear/api:1.8",
            ContainerState.Running, "Up 2 hours", new PortBinding(4000, 4000));
        AddComposeContainer("ashenmoon-stack", "db", stack, "postgres:16",
            ContainerState.Running, "Up 2 hours · healthy", new PortBinding(5432, 5432));
        AddComposeContainer("ashenmoon-stack", "redis", stack, "redis:7-alpine",
            ContainerState.Running, "Up 2 hours", new PortBinding(6379, 6379));

        const string mon = "~/dev/infra/monitoring/compose.yaml";
        AddComposeContainer("monitoring", "prometheus", mon, "prom/prometheus:v2.54",
            ContainerState.Running, "Up 40 minutes", new PortBinding(9090, 9090));
        AddComposeContainer("monitoring", "grafana", mon, "grafana/grafana:11.2.0",
            ContainerState.Exited, "Exited (1)", new PortBinding(3000, 3000));

        // A container owned by another Kontena-ecosystem app (SQL Explorer), via labels.
        AddManagedContainer("sqlx-postgres-dev", "postgres:16", ContainerState.Running,
            "Up 1 hour", "sqlexplorer", new PortBinding(55432, 5432));
    }

    private void AddManagedContainer(
        string name, string image, ContainerState state, string status, string source, params PortBinding[] ports)
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
            Labels = new Dictionary<string, string>
            {
                [ContainerSummary.ManagedLabel] = "true",
                [ContainerSummary.SourceLabel] = source,
            },
            CreatedAt = DateTimeOffset.UtcNow,
            Backend = Backend,
        };
    }

    private void AddComposeContainer(
        string project, string service, string configFile, string image,
        ContainerState state, string status, params PortBinding[] ports)
    {
        var id = NextId();
        _containers[id] = new ContainerSummary
        {
            Id = id,
            Name = $"{project}-{service}-1",
            Image = image,
            State = state,
            Status = status,
            Ports = ports,
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = project,
                ["com.docker.compose.service"] = service,
                ["com.docker.compose.project.config_files"] = configFile,
            },
            CreatedAt = DateTimeOffset.UtcNow,
            Backend = Backend,
        };
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
