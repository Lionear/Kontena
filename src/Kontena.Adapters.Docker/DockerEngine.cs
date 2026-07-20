using System.Formats.Tar;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Kontena.Core.Errors;
using Kontena.Core.Models;
using Kontena.Engines;
using KontenaState = Kontena.Core.Models.ContainerState;
using KontenaPort = Kontena.Core.Models.PortBinding;
using DockerPortBinding = Docker.DotNet.Models.PortBinding;
using DockerRestartPolicy = Docker.DotNet.Models.RestartPolicy;

namespace Kontena.Adapters.Docker;

/// <summary>
/// CEAL implementation backed by the Docker Engine API (via Docker.DotNet).
/// Transport: Unix socket on Linux/macOS, named pipe on Windows.
/// </summary>
public sealed class DockerEngine : IContainerEngine, IDisposable
{
    private readonly DockerClient _client;
    private readonly Uri _endpoint;
    private readonly string _backend;
    private readonly string _displayName;

    /// <summary>
    /// The Docker Engine API is also spoken by Podman, so this adapter serves both:
    /// pass the Podman socket plus a "podman"/"Podman" identity to reuse it.
    /// </summary>
    public DockerEngine(Uri? endpoint = null, string backend = "docker", string displayName = "Docker")
    {
        _endpoint = endpoint ?? DefaultEndpoint();
        _backend = backend;
        _displayName = displayName;
        _client = new DockerClientConfiguration(_endpoint).CreateClient();
    }

    public string Backend => _backend;

    public EngineCapabilities Capabilities { get; } = new()
    {
        Rootless = false,
        SupportsBuild = true,
        SupportsCompose = false,
        SupportsExec = true,
        SupportsPrune = true,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = true,
    };

    private static Uri DefaultEndpoint() => OperatingSystem.IsWindows()
        ? new Uri("npipe://./pipe/docker_engine")
        : new Uri("unix:///var/run/docker.sock");

    public ValueTask<EngineInfo> GetInfoAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var version = await _client.System.GetVersionAsync(ct).ConfigureAwait(false);
            return new EngineInfo
            {
                Backend = _backend,
                DisplayName = _displayName,
                Version = version.Version,
                Endpoint = _endpoint.ToString(),
                ConnectionState = EngineConnectionState.Connected,
            };
        });

    public ValueTask PingAsync(CancellationToken ct = default) =>
        Exec(() => _client.System.PingAsync(ct));

    // ── Containers ──────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        bool all = true, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var list = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = all }, ct).ConfigureAwait(false);
            IReadOnlyList<ContainerSummary> result = list.Select(MapContainer).ToList();
            return result;
        });

    public ValueTask<string> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default) =>
        Exec(async () =>
        {
            if (!await ImageExistsAsync(request.Image, ct).ConfigureAwait(false))
                await PullCoreAsync(request.Image, new NullProgress(), ct).ConfigureAwait(false);

            var exposed = new Dictionary<string, EmptyStruct>();
            var bindings = new Dictionary<string, IList<DockerPortBinding>>();
            foreach (var p in request.Ports)
            {
                var key = $"{p.ContainerPort}/{p.Protocol}";
                exposed[key] = default;
                bindings[key] = [new DockerPortBinding { HostPort = p.HostPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) }];
            }

            var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = request.Image,
                Name = request.Name,
                Env = request.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                ExposedPorts = exposed,
                HostConfig = new HostConfig
                {
                    PortBindings = bindings,
                    Binds = request.Volumes.Select(kv => $"{kv.Key}:{kv.Value}").ToList(),
                    NetworkMode = request.Network,
                    RestartPolicy = new DockerRestartPolicy { Name = MapRestart(request.RestartPolicy) },
                },
            }, ct).ConfigureAwait(false);

            if (request.Start)
                await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct).ConfigureAwait(false);

            return created.ID;
        });

    public ValueTask StartContainerAsync(string id, CancellationToken ct = default) =>
        Exec(async () => { await _client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct).ConfigureAwait(false); });

    public ValueTask StopContainerAsync(string id, CancellationToken ct = default) =>
        Exec(async () => { await _client.Containers.StopContainerAsync(id, new ContainerStopParameters(), ct).ConfigureAwait(false); });

    public ValueTask RestartContainerAsync(string id, CancellationToken ct = default) =>
        Exec(() => _client.Containers.RestartContainerAsync(id, new ContainerRestartParameters(), ct));

    public ValueTask PauseContainerAsync(string id, CancellationToken ct = default) =>
        Exec(() => _client.Containers.PauseContainerAsync(id, ct));

    public ValueTask UnpauseContainerAsync(string id, CancellationToken ct = default) =>
        Exec(() => _client.Containers.UnpauseContainerAsync(id, ct));

    public ValueTask RemoveContainerAsync(string id, bool force = false, CancellationToken ct = default) =>
        Exec(() => _client.Containers.RemoveContainerAsync(id,
            new ContainerRemoveParameters { Force = force }, ct));

    public ValueTask<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var r = await _client.Containers.InspectContainerAsync(id, ct).ConfigureAwait(false);
            return MapInspect(r);
        });

    public ValueTask<PruneResult> PruneContainersAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var response = await _client.Containers.PruneContainersAsync(new ContainersPruneParameters(), ct).ConfigureAwait(false);
            return new PruneResult(response.ContainersDeleted?.Count ?? 0, (long)response.SpaceReclaimed);
        });

    public ValueTask<int> ExecAsync(string id, ExecRequest request, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var exec = await _client.Exec.ExecCreateContainerAsync(id, new ContainerExecCreateParameters
            {
                Cmd = request.Command.ToList(),
                AttachStdout = true,
                AttachStderr = true,
                Tty = request.Tty,
                WorkingDir = request.WorkingDirectory,
            }, ct).ConfigureAwait(false);

            await _client.Exec.StartContainerExecAsync(exec.ID, ct).ConfigureAwait(false);

            ContainerExecInspectResponse inspect;
            do
            {
                inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, ct).ConfigureAwait(false);
                if (inspect.Running)
                    await Task.Delay(100, ct).ConfigureAwait(false);
            }
            while (inspect.Running);

            return (int)inspect.ExitCode;
        });

    public ValueTask<IExecSession> StartExecSessionAsync(
        string id, ExecRequest request, CancellationToken ct = default) =>
        Exec<IExecSession>(async () =>
        {
            var exec = await _client.Exec.ExecCreateContainerAsync(id, new ContainerExecCreateParameters
            {
                Cmd = request.Command.ToList(),
                AttachStdin = true,
                AttachStdout = true,
                AttachStderr = true,
                Tty = request.Tty,
                WorkingDir = request.WorkingDirectory,
            }, ct).ConfigureAwait(false);

            var stream = await _client.Exec
                .StartAndAttachContainerExecAsync(exec.ID, request.Tty, ct).ConfigureAwait(false);

            return new DockerExecSession(_client, exec.ID, stream);
        });

    // ── Images ──────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var list = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct).ConfigureAwait(false);
            var result = new List<ImageSummary>();
            foreach (var img in list)
            {
                var tags = img.RepoTags is { Count: > 0 } ? img.RepoTags : ["<none>:<none>"];
                foreach (var repoTag in tags)
                {
                    var (repo, tag) = SplitRepoTag(repoTag);
                    result.Add(new ImageSummary
                    {
                        Id = img.ID,
                        Repository = repo,
                        Tag = tag,
                        SizeBytes = img.Size,
                        CreatedAt = img.Created,
                        InUse = img.Containers > 0,
                    });
                }
            }

            return (IReadOnlyList<ImageSummary>)result;
        });

    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<JSONMessage>(new UnboundedChannelOptions { SingleReader = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pump = Task.Run(async () =>
        {
            try
            {
                await PullCoreAsync(reference, new ChannelProgress<JSONMessage>(channel.Writer), linked.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, linked.Token);

        try
        {
            await foreach (var m in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                yield return new PullProgress(
                    reference,
                    string.IsNullOrEmpty(m.Status) ? m.ProgressMessage ?? string.Empty : m.Status,
                    m.Progress?.Current,
                    m.Progress?.Total);
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await SwallowAsync(pump).ConfigureAwait(false);
        }
    }

    public ValueTask RemoveImageAsync(string id, bool force = false, CancellationToken ct = default) =>
        Exec(async () => { await _client.Images.DeleteImageAsync(id, new ImageDeleteParameters { Force = force }, ct).ConfigureAwait(false); });

    public ValueTask TagImageAsync(string id, string newTag, CancellationToken ct = default) =>
        Exec(() =>
        {
            var (repo, tag) = SplitRepoTag(newTag);
            return _client.Images.TagImageAsync(id, new ImageTagParameters { RepositoryName = repo, Tag = tag }, ct);
        });

    public async ValueTask<ImageConfig?> InspectImageAsync(string reference, CancellationToken ct = default)
    {
        ImageInspectResponse image;
        try
        {
            image = await _client.Images.InspectImageAsync(reference, ct).ConfigureAwait(false);
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
        catch (DockerApiException api) when (api.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw Map(ex);
        }

        var ports = new List<KontenaPort>();
        foreach (var key in image.Config?.ExposedPorts?.Keys ?? [])
        {
            // key looks like "5432/tcp"
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            var portText = slash >= 0 ? key[..slash] : key;
            var protocol = slash >= 0 ? key[(slash + 1)..] : "tcp";
            if (int.TryParse(portText, out var port))
                ports.Add(new KontenaPort(null, port, protocol));
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in image.Config?.Env ?? [])
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                env[entry[..eq]] = entry[(eq + 1)..];
        }

        return new ImageConfig
        {
            ExposedPorts = ports,
            Volumes = image.Config?.Volumes?.Keys.ToList() ?? [],
            Environment = env,
        };
    }

    public ValueTask<PruneResult> PruneImagesAsync(bool allUnused = true, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var parameters = new ImagesPruneParameters();
            if (allUnused)
            {
                // dangling=false prunes every image not used by a container (docker image prune -a).
                parameters.Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["dangling"] = new Dictionary<string, bool> { ["false"] = true },
                };
            }

            var response = await _client.Images.PruneImagesAsync(parameters, ct).ConfigureAwait(false);
            return new PruneResult(response.ImagesDeleted?.Count ?? 0, (long)response.SpaceReclaimed);
        });

    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(
        BuildRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = new ImageBuildParameters
        {
            Tags = [request.Tag],
            Dockerfile = request.Dockerfile,
            Target = string.IsNullOrWhiteSpace(request.Target) ? null : request.Target,
            BuildArgs = new Dictionary<string, string>(request.BuildArgs),
            NoCache = request.NoCache,
            Pull = request.Pull ? "true" : null,
            Remove = true,
        };

        // Docker's /build endpoint wants the context as a tar stream.
        using var context = new MemoryStream();
        var tarError = default(string);
        try
        {
            TarFile.CreateFromDirectory(request.ContextPath, context, includeBaseDirectory: false);
            context.Position = 0;
        }
        catch (Exception ex)
        {
            tarError = $"Could not read build context: {ex.Message}";
        }

        if (tarError is not null)
        {
            yield return new BuildProgress(string.Empty, tarError);
            yield break;
        }

        var channel = Channel.CreateUnbounded<BuildProgress>();
        var progress = new ChannelBuildProgress(channel.Writer);

        var build = Task.Run(async () =>
        {
            try
            {
                await _client.Images.BuildImageFromDockerfileAsync(
                    parameters, context, null, null, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new BuildProgress(string.Empty, ex.Message));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;

        try { await build.ConfigureAwait(false); }
        catch { /* surfaced through the channel already */ }
    }

    /// <summary>Forwards Docker build messages to a channel, in order, on the reading thread.</summary>
    private sealed class ChannelBuildProgress(ChannelWriter<BuildProgress> writer) : IProgress<JSONMessage>
    {
        public void Report(JSONMessage message)
        {
            var error = message.Error?.Message ?? message.ErrorMessage;
            if (!string.IsNullOrEmpty(error))
            {
                writer.TryWrite(new BuildProgress(string.Empty, error));
                return;
            }

            var text = (message.Stream ?? message.Status ?? string.Empty).TrimEnd('\n', '\r');
            if (text.Length > 0)
                writer.TryWrite(new BuildProgress(text));
        }
    }

    // ── Volumes ─────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<VolumeSummary>> ListVolumesAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var response = await _client.Volumes.ListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<VolumeSummary> result = (response.Volumes ?? []).Select(v => new VolumeSummary
            {
                Name = v.Name,
                Driver = v.Driver,
                Mountpoint = v.Mountpoint,
            }).ToList();
            return result;
        });

    public ValueTask<VolumeSummary> CreateVolumeAsync(
        CreateVolumeRequest request, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var v = await _client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = request.Name,
                Driver = request.Driver,
            }, ct).ConfigureAwait(false);
            return new VolumeSummary { Name = v.Name, Driver = v.Driver, Mountpoint = v.Mountpoint };
        });

    public ValueTask RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default) =>
        Exec(() => _client.Volumes.RemoveAsync(name, force, ct));

    public ValueTask<PruneResult> PruneVolumesAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var response = await _client.Volumes.PruneAsync(new VolumesPruneParameters(), ct).ConfigureAwait(false);
            return new PruneResult(response.VolumesDeleted?.Count ?? 0, (long)response.SpaceReclaimed);
        });

    // ── Networks ────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var list = await _client.Networks.ListNetworksAsync(new NetworksListParameters(), ct).ConfigureAwait(false);
            IReadOnlyList<NetworkSummary> result = list.Select(MapNetwork).ToList();
            return result;
        });

    public ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var response = await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = request.Name,
                Driver = request.Driver,
            }, ct).ConfigureAwait(false);
            return new NetworkSummary { Id = response.ID, Name = request.Name, Driver = request.Driver, Subnet = request.Subnet };
        });

    public ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        Exec(() => _client.Networks.DeleteNetworkAsync(id, ct));

    // ── Streams ─────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<LogEntry> StreamLogsAsync(
        string id, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool tty;
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(id, ct).ConfigureAwait(false);
            tty = inspect.Config.Tty;
        }
        catch (Exception ex)
        {
            throw Map(ex);
        }

        var stream = await Exec(() => _client.Containers.GetContainerLogsAsync(id, tty, new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = follow,
            Timestamps = false,
            Tail = follow ? "200" : "all",
        }, ct)).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var partial = new Dictionary<LogSource, StringBuilder>
        {
            [LogSource.Stdout] = new(),
            [LogSource.Stderr] = new(),
        };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                MultiplexedStream.ReadResult read;
                try
                {
                    read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw Map(ex);
                }

                if (read.EOF)
                    break;

                var source = read.Target == MultiplexedStream.TargetStream.StandardError
                    ? LogSource.Stderr
                    : LogSource.Stdout;

                var sb = partial[source];
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));

                foreach (var line in DrainLines(sb))
                    yield return new LogEntry(DateTimeOffset.UtcNow, source, line);
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    public async IAsyncEnumerable<ContainerStats> StreamStatsAsync(
        string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<ContainerStatsResponse>(new UnboundedChannelOptions { SingleReader = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pump = Task.Run(async () =>
        {
            try
            {
                await _client.Containers.GetContainerStatsAsync(id,
                    new ContainerStatsParameters { Stream = true },
                    new ChannelProgress<ContainerStatsResponse>(channel.Writer), linked.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, linked.Token);

        try
        {
            await foreach (var raw in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                yield return MapStats(id, raw);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await SwallowAsync(pump).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<EngineEvent> StreamEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pump = Task.Run(async () =>
        {
            try
            {
                await _client.System.MonitorEventsAsync(new ContainerEventsParameters(),
                    new ChannelProgress<Message>(channel.Writer), linked.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, linked.Token);

        try
        {
            await foreach (var m in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                yield return MapEvent(m);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await SwallowAsync(pump).ConfigureAwait(false);
        }
    }

    public void Dispose() => _client.Dispose();

    // ── Mapping helpers ─────────────────────────────────────────────────────

    private ContainerSummary MapContainer(ContainerListResponse c) => new()
    {
        Id = c.ID,
        Name = c.Names is { Count: > 0 } ? c.Names[0].TrimStart('/') : c.ID[..Math.Min(12, c.ID.Length)],
        Image = c.Image,
        State = MapState(c.State),
        Status = c.Status ?? string.Empty,
        Ports = MapPorts(c.Ports),
        Labels = c.Labels is { Count: > 0 }
            ? new Dictionary<string, string>(c.Labels)
            : new Dictionary<string, string>(),
        CreatedAt = c.Created,
        Backend = Backend,
    };

    private static KontenaState MapState(string? state) => state switch
    {
        "created" => KontenaState.Created,
        "running" => KontenaState.Running,
        "paused" => KontenaState.Paused,
        "restarting" => KontenaState.Restarting,
        "exited" => KontenaState.Exited,
        "dead" => KontenaState.Dead,
        "removing" => KontenaState.Removing,
        _ => KontenaState.Unknown,
    };

    private static List<KontenaPort> MapPorts(IList<Port>? ports)
    {
        if (ports is null || ports.Count == 0)
            return [];

        var seen = new HashSet<(int?, ushort, string)>();
        var result = new List<KontenaPort>();
        foreach (var p in ports)
        {
            int? host = p.PublicPort == 0 ? null : p.PublicPort;
            var key = (host, p.PrivatePort, p.Type);
            if (seen.Add(key))
                result.Add(new KontenaPort(host, p.PrivatePort, p.Type));
        }

        return result;
    }

    private static NetworkSummary MapNetwork(NetworkResponse n) => new()
    {
        Id = n.ID,
        Name = n.Name,
        Driver = n.Driver,
        Scope = n.Scope,
        Subnet = n.IPAM?.Config is { Count: > 0 } cfg ? cfg[0].Subnet : null,
        AttachedContainers = n.Containers?.Values.Select(e => e.Name).Where(x => !string.IsNullOrEmpty(x)).ToList() ?? [],
        IsBuiltIn = n.Name is "bridge" or "host" or "none",
    };

    private static ContainerStats MapStats(string id, ContainerStatsResponse s)
    {
        double cpuDelta = (double)s.CPUStats.CPUUsage.TotalUsage - s.PreCPUStats.CPUUsage.TotalUsage;
        double sysDelta = (double)s.CPUStats.SystemUsage - s.PreCPUStats.SystemUsage;
        double onlineCpus = s.CPUStats.OnlineCPUs != 0
            ? s.CPUStats.OnlineCPUs
            : s.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1;
        double cpuPercent = sysDelta > 0 && cpuDelta > 0 ? cpuDelta / sysDelta * onlineCpus * 100.0 : 0;

        long netRx = 0, netTx = 0;
        if (s.Networks is not null)
        {
            foreach (var net in s.Networks.Values)
            {
                netRx += (long)net.RxBytes;
                netTx += (long)net.TxBytes;
            }
        }

        long blkRead = 0, blkWrite = 0;
        if (s.BlkioStats?.IoServiceBytesRecursive is not null)
        {
            foreach (var e in s.BlkioStats.IoServiceBytesRecursive)
            {
                if (string.Equals(e.Op, "Read", StringComparison.OrdinalIgnoreCase)) blkRead += (long)e.Value;
                else if (string.Equals(e.Op, "Write", StringComparison.OrdinalIgnoreCase)) blkWrite += (long)e.Value;
            }
        }

        return new ContainerStats
        {
            ContainerId = id,
            CpuPercent = cpuPercent,
            MemoryUsedBytes = (long)s.MemoryStats.Usage,
            MemoryLimitBytes = (long)s.MemoryStats.Limit,
            NetRxBytes = netRx,
            NetTxBytes = netTx,
            BlockReadBytes = blkRead,
            BlockWriteBytes = blkWrite,
        };
    }

    private static EngineEvent MapEvent(Message m) => new(
        m.Action switch
        {
            "create" => EngineEventType.Created,
            "start" => EngineEventType.Started,
            "stop" or "kill" => EngineEventType.Stopped,
            "die" => EngineEventType.Died,
            "pause" => EngineEventType.Paused,
            "unpause" => EngineEventType.Unpaused,
            "destroy" or "remove" => EngineEventType.Removed,
            "pull" => EngineEventType.Pulled,
            _ => EngineEventType.Unknown,
        },
        m.Type switch
        {
            "image" => ResourceKind.Image,
            "volume" => ResourceKind.Volume,
            "network" => ResourceKind.Network,
            _ => ResourceKind.Container,
        },
        m.Actor?.ID ?? m.ID ?? string.Empty,
        DateTimeOffset.FromUnixTimeSeconds(m.Time));

    private static RestartPolicyKind MapRestart(Core.Models.RestartPolicy policy) => policy switch
    {
        Core.Models.RestartPolicy.Always => RestartPolicyKind.Always,
        Core.Models.RestartPolicy.OnFailure => RestartPolicyKind.OnFailure,
        Core.Models.RestartPolicy.UnlessStopped => RestartPolicyKind.UnlessStopped,
        _ => RestartPolicyKind.No,
    };

    private static Core.Models.RestartPolicy MapRestart(RestartPolicyKind? kind) => kind switch
    {
        RestartPolicyKind.Always => Core.Models.RestartPolicy.Always,
        RestartPolicyKind.OnFailure => Core.Models.RestartPolicy.OnFailure,
        RestartPolicyKind.UnlessStopped => Core.Models.RestartPolicy.UnlessStopped,
        _ => Core.Models.RestartPolicy.No,
    };

    private static ContainerInspect MapInspect(ContainerInspectResponse r)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in r.Config?.Env ?? [])
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                env[entry[..eq]] = entry[(eq + 1)..];
            else
                env[entry] = string.Empty;
        }

        var command = new List<string>();
        if (r.Config?.Entrypoint is { } entrypoint)
            command.AddRange(entrypoint);
        if (r.Config?.Cmd is { } cmd)
            command.AddRange(cmd);

        var mounts = (r.Mounts ?? [])
            .Select(m => new InspectMount(m.Type ?? string.Empty, m.Source ?? string.Empty,
                m.Destination ?? string.Empty, m.RW))
            .ToList();

        var networks = (r.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
            .Select(kv => new InspectNetwork(kv.Key, kv.Value?.IPAddress ?? string.Empty,
                kv.Value?.Gateway ?? string.Empty))
            .ToList();

        return new ContainerInspect
        {
            Id = r.ID,
            Name = (r.Name ?? string.Empty).TrimStart('/'),
            Image = r.Config?.Image ?? string.Empty,
            ImageId = r.Image ?? string.Empty,
            State = MapState(r.State?.Status),
            Status = r.State?.Status ?? string.Empty,
            CreatedAt = r.Created,
            StartedAt = ParseDockerDate(r.State?.StartedAt),
            FinishedAt = ParseDockerDate(r.State?.FinishedAt),
            ExitCode = (int)(r.State?.ExitCode ?? 0),
            Pid = (int)(r.State?.Pid ?? 0),
            RestartPolicy = MapRestart(r.HostConfig?.RestartPolicy?.Name),
            Command = string.Join(" ", command),
            WorkingDirectory = r.Config?.WorkingDir ?? string.Empty,
            User = r.Config?.User ?? string.Empty,
            EnvironmentVariables = env,
            Labels = r.Config?.Labels is { } labels
                ? new Dictionary<string, string>(labels)
                : new Dictionary<string, string>(),
            Mounts = mounts,
            Networks = networks,
        };
    }

    /// <summary>Docker returns RFC3339 timestamps; a zero value means "never".</summary>
    private static DateTimeOffset? ParseDockerDate(string? value)
    {
        if (string.IsNullOrEmpty(value) || !DateTimeOffset.TryParse(value, out var when))
            return null;

        return when.Year <= 1 ? null : when;
    }

    private async Task<bool> ImageExistsAsync(string reference, CancellationToken ct)
    {
        try
        {
            await _client.Images.InspectImageAsync(reference, ct).ConfigureAwait(false);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
        catch (DockerApiException api) when (api.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private Task PullCoreAsync(string reference, IProgress<JSONMessage> progress, CancellationToken ct)
    {
        var (repo, tag) = SplitRepoTag(reference);
        return _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag },
            authConfig: null, progress, ct);
    }

    private static (string Repository, string Tag) SplitRepoTag(string reference)
    {
        var idx = reference.LastIndexOf(':');
        return idx > 0 && !reference[(idx + 1)..].Contains('/')
            ? (reference[..idx], reference[(idx + 1)..])
            : (reference, "latest");
    }

    private static List<string> DrainLines(StringBuilder sb)
    {
        var text = sb.ToString();
        int start = 0, nl;
        var lines = new List<string>();
        while ((nl = text.IndexOf('\n', start)) >= 0)
        {
            lines.Add(text[start..nl].TrimEnd('\r'));
            start = nl + 1;
        }

        sb.Clear();
        if (start < text.Length)
            sb.Append(text[start..]);

        return lines;
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* draining a cancelled pump */ }
    }

    // ── Error mapping ───────────────────────────────────────────────────────

    private static async ValueTask<T> Exec<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (Exception ex) { throw Map(ex); }
    }

    private static async ValueTask Exec(Func<Task> call)
    {
        try { await call().ConfigureAwait(false); }
        catch (Exception ex) { throw Map(ex); }
    }

    private static EngineException Map(Exception ex) => ex switch
    {
        EngineException ee => ee,
        DockerContainerNotFoundException => new ResourceNotFoundException(ex.Message, ex),
        DockerImageNotFoundException => new ResourceNotFoundException(ex.Message, ex),
        DockerApiException api when api.StatusCode == HttpStatusCode.NotFound => new ResourceNotFoundException(api.Message, api),
        DockerApiException api when api.StatusCode == HttpStatusCode.Forbidden => new EnginePermissionException(api.Message, api),
        DockerApiException api => new EngineException(api.Message, api),
        TimeoutException => new EngineUnreachableException("Docker did not respond in time.", ex),
        HttpRequestException => new EngineUnreachableException("Cannot reach the Docker engine.", ex),
        System.Net.Sockets.SocketException => new EngineUnreachableException("Cannot reach the Docker engine.", ex),
        IOException => new EngineUnreachableException("Lost connection to the Docker engine.", ex),
        _ => new EngineException(ex.Message, ex),
    };

    /// <summary>Writes progress reports straight into a channel, preserving order.</summary>
    private sealed class ChannelProgress<T> : IProgress<T>
    {
        private readonly ChannelWriter<T> _writer;
        public ChannelProgress(ChannelWriter<T> writer) => _writer = writer;
        public void Report(T value) => _writer.TryWrite(value);
    }

    private sealed class NullProgress : IProgress<JSONMessage>
    {
        public void Report(JSONMessage value) { }
    }
}
