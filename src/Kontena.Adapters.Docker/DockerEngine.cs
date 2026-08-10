using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Kontena.Sdk.Errors;
using Kontena.Sdk.Models;
using Kontena.Sdk;
using KontenaState = Kontena.Sdk.Models.ContainerState;
using KontenaPort = Kontena.Sdk.Models.PortBinding;
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
    private readonly IAsyncDisposable? _attached;
    private readonly string _backend;
    private readonly string _displayName;

    /// <summary>
    /// The Docker Engine API is also spoken by Podman, so this adapter serves both:
    /// pass the Podman socket plus a "podman"/"Podman" identity to reuse it.
    /// </summary>
    /// <param name="certificateDirectory">
    /// Directory holding <c>ca.pem</c>, <c>cert.pem</c> and <c>key.pem</c> for a TLS endpoint — the
    /// <c>DOCKER_CERT_PATH</c> layout, so an existing setup can be pointed at rather than rebuilt. Null
    /// for a local socket or an explicitly insecure TCP endpoint (KON-46).
    /// </param>
    /// <param name="attached">
    /// Something whose lifetime belongs to this engine and is disposed with it — the SSH tunnel a remote
    /// engine speaks through (KON-46). The tunnel must outlive every call and die with the connection, and
    /// tying it to the client is the only way that is not a second lifetime to get wrong.
    /// </param>
    public DockerEngine(
        Uri? endpoint = null, string backend = "docker", string displayName = "Docker",
        string? certificateDirectory = null, IAsyncDisposable? attached = null)
    {
        _attached = attached;
        _endpoint = endpoint ?? DefaultEndpoint();
        _backend = backend;
        _displayName = displayName;

        var credentials = LoadCertificates(certificateDirectory);
        _client = new DockerClientConfiguration(_endpoint, credentials).CreateClient();
    }

    private static MutualTlsCredentials? LoadCertificates(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? null : MutualTlsCredentials.FromDirectory(directory);

    public string Backend => _backend;

    public EngineCapabilities Capabilities { get; } = new()
    {
        Rootless = false,
        SupportsBuild = true,
        SupportsCompose = true,
        SupportsExec = true,
        SupportsRestartPolicy = true,
        SupportsPrune = true,
        SupportsGpu = false,
        SupportsStats = true,
        SupportsEvents = true,
        SupportsVolumeBrowse = true,
    };

    private static Uri DefaultEndpoint() => OperatingSystem.IsWindows()
        ? new Uri("npipe://./pipe/docker_engine")
        : new Uri("unix:///var/run/docker.sock");

    public ValueTask<BackendInfo> GetInfoAsync(CancellationToken ct = default) =>
        Exec(async () =>
        {
            var version = await _client.System.GetVersionAsync(ct).ConfigureAwait(false);
            return new BackendInfo
            {
                Backend = _backend,
                DisplayName = _displayName,
                Kind = "container engine",
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
                // Anonymous on purpose: a credential has no business in CreateContainerRequest, which is
                // a record that gets passed around and logged. A private image is pulled first — the Run
                // dialog does exactly that — and this only covers the case where it is already local or
                // public.
                await PullCoreAsync(request.Image, credential: null, new NullProgress(), ct).ConfigureAwait(false);

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

                // Null rather than an empty list: to this API an empty Cmd means "no command", not
                // "keep the image's", and that is a container which starts and stops again.
                Entrypoint = request.Entrypoint.Count > 0 ? request.Entrypoint.ToList() : null,
                Cmd = request.Command.Count > 0 ? request.Command.ToList() : null,
                WorkingDir = request.WorkingDirectory,
                User = request.User,
                Labels = request.Labels.Count > 0
                    ? new Dictionary<string, string>(request.Labels)
                    : null,
                HostConfig = new HostConfig
                {
                    PortBindings = bindings,
                    Binds = request.Mounts
                        .Select(m => m.ReadOnly ? $"{m.Source}:{m.Target}:ro" : $"{m.Source}:{m.Target}")
                        .ToList(),
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
                        CreatedAt = EngineTimestamp.From(img.Created),
                        InUse = img.Containers > 0,
                    });
                }
            }

            return (IReadOnlyList<ImageSummary>)result;
        });

    public async IAsyncEnumerable<PullProgress> PullImageAsync(
        string reference, RegistryCredential? credential = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<JSONMessage>(new UnboundedChannelOptions { SingleReader = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pump = Task.Run(async () =>
        {
            try
            {
                await PullCoreAsync(reference, credential, new ChannelProgress<JSONMessage>(channel.Writer), linked.Token).ConfigureAwait(false);
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
        if (!Directory.Exists(request.ContextPath))
        {
            yield return new BuildProgress(string.Empty, $"Build context not found: {request.ContextPath}");
            yield break;
        }

        // Drive the engine's `build` CLI from the context dir: it honors .dockerignore natively
        // and streams the context itself — no in-memory tar. Docker gets BuildKit's fine-grained,
        // line-based progress; Podman drives Buildah (which prints its own STEP output).
        var isDocker = string.Equals(_backend, "docker", StringComparison.Ordinal);
        var dockerfile = string.IsNullOrWhiteSpace(request.Dockerfile) ? "Dockerfile" : request.Dockerfile;

        var args = new List<string> { "build" };
        if (isDocker)
            args.Add("--progress=plain");
        args.Add("-f"); args.Add(dockerfile);
        args.Add("-t"); args.Add(request.Tag);
        if (!string.IsNullOrWhiteSpace(request.Target)) { args.Add("--target"); args.Add(request.Target!); }
        if (request.NoCache) args.Add("--no-cache");
        if (request.Pull) args.Add("--pull");
        foreach (var (key, value) in request.BuildArgs)
        {
            args.Add("--build-arg");
            args.Add($"{key}={value}");
        }
        args.Add("."); // context = working dir; its .dockerignore applies

        var env = isDocker
            ? new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1" }
            : null;

        await foreach (var line in RunCliAsync(_backend, args, request.ContextPath, env, "build", ct)
                           .ConfigureAwait(false))
            yield return new BuildProgress(line.Text, line.Error);
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


    /// <summary>
    /// Lists a volume's contents by mounting it into a container that is created but never started.
    /// <para>
    /// Docker has no API for reading a volume: its contents only exist to containers. The usual trick
    /// is to <c>run</c> a shell and parse <c>ls</c>, which needs an image with a shell and a process
    /// that actually starts. This does neither — the archive endpoint reads from a *created* container,
    /// so nothing is executed, nothing writes, and the volume is mounted read-only. It also means no
    /// image needs a shell: any locally present image will do as somewhere to hang the mount.
    /// </para>
    /// <para>
    /// The archive of a directory is a recursive tar, and only its headers are read — the entry bodies
    /// are skipped without being buffered. Even so it is bounded: a volume with a million files would
    /// otherwise be a long silence, so listing stops at <see cref="MaxEntries"/> and says it did.
    /// </para>
    /// </summary>
    public ValueTask<VolumeListing> BrowseVolumeAsync(
        string name, string path = "/", CancellationToken ct = default) =>
        Exec(async () =>
        {
            var target = NormalizeBrowsePath(path);

            // Any image will do — this container is never started, so nothing in it runs. Preferring a
            // small one keeps the create cheap on engines that have to unpack layers.
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct)
                .ConfigureAwait(false);
            var image = images
                .Where(i => i.RepoTags?.Count > 0)
                .OrderBy(i => i.Size)
                .Select(i => i.RepoTags![0])
                .FirstOrDefault()
                ?? throw new EngineException(
                    "Browsing a volume needs an image to mount it into, and this engine has none. "
                    + "Pull any image first — nothing from it is run.");

            var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Labels = new Dictionary<string, string> { ["kontena.purpose"] = "volume-browse" },
                HostConfig = new HostConfig
                {
                    AutoRemove = false,
                    Binds = [$"{name}:{MountPoint}:ro"],
                },
            }, ct).ConfigureAwait(false);

            try
            {
                var archivePath = MountPoint + target;
                var response = await _client.Containers.GetArchiveFromContainerAsync(
                    created.ID,
                    new GetArchiveFromContainerParameters { Path = archivePath },
                    statOnly: false,
                    ct).ConfigureAwait(false);

                await using var stream = response.Stream;
                return ReadListing(stream, target);
            }
            finally
            {
                // The container exists only for the mount, so it goes whatever happened above.
                try
                {
                    await _client.Containers.RemoveContainerAsync(
                        created.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Leaving a stopped, never-started container behind is untidy, not harmful, and it
                    // must not turn a successful listing into an error.
                }
            }
        });


    /// <summary>
    /// The immediate children of the listed directory, read from the tar the engine streams back.
    /// <para>
    /// Docker roots the archive at the last segment of the requested path, so <c>/vol/app</c> comes back
    /// as <c>app/</c>, <c>app/config.yml</c>, … Only entries exactly one level below that root are
    /// children; anything deeper belongs to a subdirectory and is skipped. Entry bodies are never
    /// copied — <c>GetNextEntry(copyData: false)</c> walks the headers and seeks past the data.
    /// </para>
    /// </summary>
    private static VolumeListing ReadListing(Stream tar, string path)
    {
        var entries = new List<VolumeEntry>();
        var truncated = false;

        using var reader = new TarReader(tar);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            var relative = entry.Name.Replace('\\', '/').TrimEnd('/');
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
                continue;                                  // the archive root itself, not a child

            var inside = relative[(slash + 1)..];
            if (inside.Length == 0 || inside.Contains('/', StringComparison.Ordinal))
                continue;                                  // deeper than one level

            var isDirectory = entry.EntryType is TarEntryType.Directory;
            entries.Add(new VolumeEntry(
                inside,
                isDirectory,
                isDirectory ? 0 : entry.Length,
                entry.ModificationTime == default ? null : entry.ModificationTime));
        }

        return new VolumeListing(
            path.Length == 0 ? "/" : path,
            [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)],
            truncated);
    }

    private const string MountPoint = "/kontena-volume";
    private const int MaxEntries = 5_000;

    /// <summary>
    /// The path inside the volume, as an absolute path with no trailing slash and no way out of the
    /// mount. <c>..</c> segments are resolved here rather than passed on: the archive endpoint would
    /// happily read the container's own filesystem outside the mount.
    /// </summary>
    internal static string NormalizeBrowsePath(string path)
    {
        var parts = (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        return stack.Count == 0 ? string.Empty : "/" + string.Join('/', stack);
    }

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

            // The list endpoint never carries Containers — not even with verbose — so which containers are
            // attached has to come from inspecting each network. Without this the ATTACHED column reads
            // "none" on every network however many containers are on it, which is what it did until now.
            // One extra call per network: there are a handful of them, and this column is the reason the
            // page exists.
            var inspected = await Task.WhenAll(list.Select(async network =>
            {
                try
                {
                    var detail = await _client.Networks.InspectNetworkAsync(network.ID, ct).ConfigureAwait(false);
                    return MapNetwork(network) with
                    {
                        AttachedContainers =
                        [
                            .. (detail.Containers ?? new Dictionary<string, EndpointResource>())
                                .Select(c => string.IsNullOrEmpty(c.Value?.Name) ? c.Key : c.Value.Name)
                                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                        ],
                    };
                }
                catch (Exception)
                {
                    // A network removed between the list and the inspect, or one this user cannot inspect:
                    // report it without its attachments rather than failing the whole page.
                    return MapNetwork(network);
                }
            })).ConfigureAwait(false);

            IReadOnlyList<NetworkSummary> result = inspected;
            return result;
        });

    public ValueTask<NetworkSummary> CreateNetworkAsync(
        CreateNetworkRequest request, CancellationToken ct = default) =>
        Exec(async () =>
        {
            var parameters = new NetworksCreateParameters
            {
                Name = request.Name,
                Driver = request.Driver,
            };

            // A subnet has to be sent as IPAM config or Docker assigns one from its own pool. Without
            // this, the network came back reporting the subnet that was asked for while actually
            // having a different one — the summary was describing the request, not the network.
            if (!string.IsNullOrWhiteSpace(request.Subnet))
            {
                parameters.IPAM = new IPAM
                {
                    Config = [new IPAMConfig { Subnet = request.Subnet }],
                };
            }

            var response = await _client.Networks.CreateNetworkAsync(parameters, ct).ConfigureAwait(false);

            // Read the network back rather than echoing the request: the engine decides the id, and
            // when no subnet was asked for it also decides the subnet.
            var created = await _client.Networks.InspectNetworkAsync(response.ID, ct).ConfigureAwait(false);
            return new NetworkSummary
            {
                Id = created.ID,
                Name = created.Name,
                Driver = created.Driver,
                Subnet = created.IPAM?.Config?.FirstOrDefault()?.Subnet ?? string.Empty,
            };
        });

    public ValueTask RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        Exec(() => _client.Networks.DeleteNetworkAsync(id, ct));

    public ValueTask ConnectNetworkAsync(
        string containerId, string networkId, CancellationToken ct = default) =>
        Exec(() => _client.Networks.ConnectNetworkAsync(
            networkId, new NetworkConnectParameters { Container = containerId }, ct));

    public ValueTask DisconnectNetworkAsync(
        string containerId, string networkId, bool force = false, CancellationToken ct = default) =>
        Exec(() => _client.Networks.DisconnectNetworkAsync(
            networkId, new NetworkDisconnectParameters { Container = containerId, Force = force }, ct));

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
            // Asked for, and parsed off each line below (KON-203). Without them every line carried the
            // moment Kontena read it, so a backlog of forty lines from four different days all showed
            // the same millisecond.
            Timestamps = true,
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
                    yield return LogLine.Parse(line, source, DateTimeOffset.UtcNow);
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

    // ── Compose ─────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<ComposeProgress> ComposeUpAsync(
        ComposeUpRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var file = request.ComposeFilePath;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            yield return new ComposeProgress($"Compose file not found: {file}", "not-found");
            yield break;
        }

        // `docker compose` (Compose v2 plugin); Podman speaks the same via `podman compose`.
        var args = new List<string> { "compose", "-f", file };
        if (!string.IsNullOrWhiteSpace(request.ProjectName))
        {
            args.Add("-p");
            args.Add(request.ProjectName!);
        }
        args.Add("up");
        args.Add("-d");
        if (request.Build) args.Add("--build");
        if (request.ForceRecreate) args.Add("--force-recreate");

        await foreach (var line in RunCliAsync(_backend, args, Path.GetDirectoryName(file), null, "compose", ct)
                           .ConfigureAwait(false))
            yield return new ComposeProgress(line.Text, line.Error);
    }

    /// <summary>One merged stdout/stderr line from a CLI run; <see cref="Error"/> marks a failure.</summary>
    private readonly record struct CliLine(string Text, string? Error);

    /// <summary>
    /// Run an external CLI (<c>docker</c>/<c>podman</c> with a subcommand), merging stdout and
    /// stderr into a single line stream — both matter, since Compose and BuildKit write progress
    /// to stderr. Cancellation kills the whole process tree; a non-zero exit and a missing CLI
    /// both surface as an error line. <paramref name="what"/> names the subcommand for that message.
    /// </summary>
    private static async IAsyncEnumerable<CliLine> RunCliAsync(
        string exe, IReadOnlyList<string> args, string? workingDir,
        IReadOnlyDictionary<string, string>? env, string what,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDir))
            psi.WorkingDirectory = workingDir;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (key, value) in env)
                psi.Environment[key] = value;

        var process = new Process { StartInfo = psi };

        string? startError = null;
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            startError = ex.Message;
        }

        if (startError is not null)
        {
            process.Dispose();
            yield return new CliLine(
                $"Could not start '{exe} {what}' — is the {exe} CLI installed and on PATH?  ({startError})",
                "cli-missing");
            yield break;
        }

        await using var kill = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        var channel = Channel.CreateUnbounded<CliLine>(new UnboundedChannelOptions { SingleReader = true });

        async Task PumpAsync(TextReader reader)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    channel.Writer.TryWrite(new CliLine(line, null));
            }
            catch (OperationCanceledException) { /* stream torn down on cancel */ }
        }

        var pump = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(
                    PumpAsync(process.StandardOutput),
                    PumpAsync(process.StandardError)).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* cancelled */ }
            finally
            {
                int code = -1;
                try { code = process.ExitCode; }
                catch { /* killed before exit */ }
                if (code != 0 && !ct.IsCancellationRequested)
                    channel.Writer.TryWrite(new CliLine(
                        $"{exe} {what} exited with code {code.ToString(System.Globalization.CultureInfo.InvariantCulture)}.", "exit-nonzero"));
                channel.Writer.TryComplete();
                process.Dispose();
            }
        }, ct);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            await SwallowAsync(pump).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _client.Dispose();

        // Synchronous because IContainerEngine is disposed synchronously by the shell; the tunnel's own
        // teardown is a kill and a file delete, so there is nothing here worth an async path.
        if (_attached is { } attached)
            attached.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

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
        CreatedAt = EngineTimestamp.From(c.Created),
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

    private static RestartPolicyKind MapRestart(Kontena.Sdk.Models.RestartPolicy policy) => policy switch
    {
        Kontena.Sdk.Models.RestartPolicy.Always => RestartPolicyKind.Always,
        Kontena.Sdk.Models.RestartPolicy.OnFailure => RestartPolicyKind.OnFailure,
        Kontena.Sdk.Models.RestartPolicy.UnlessStopped => RestartPolicyKind.UnlessStopped,
        _ => RestartPolicyKind.No,
    };

    private static Kontena.Sdk.Models.RestartPolicy MapRestart(RestartPolicyKind? kind) => kind switch
    {
        RestartPolicyKind.Always => Kontena.Sdk.Models.RestartPolicy.Always,
        RestartPolicyKind.OnFailure => Kontena.Sdk.Models.RestartPolicy.OnFailure,
        RestartPolicyKind.UnlessStopped => Kontena.Sdk.Models.RestartPolicy.UnlessStopped,
        _ => Kontena.Sdk.Models.RestartPolicy.No,
    };

    internal static ContainerInspect MapInspect(ContainerInspectResponse r)
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
            CreatedAt = EngineTimestamp.From(r.Created),
            StartedAt = ParseDockerDate(r.State?.StartedAt),
            FinishedAt = ParseDockerDate(r.State?.FinishedAt),
            ExitCode = (int)(r.State?.ExitCode ?? 0),
            Pid = (int)(r.State?.Pid ?? 0),
            OomKilled = r.State?.OOMKilled ?? false,
            RestartCount = (int)r.RestartCount,
            // Zero means "no limit" to the engine, and null means the same to us — reporting a limit
            // of nothing would turn an unlimited container into one that may use no memory at all.
            MemoryLimitBytes = r.HostConfig?.Memory is > 0 and var memory ? memory : null,
            Error = r.State?.Error ?? string.Empty,
            RestartPolicy = MapRestart(r.HostConfig?.RestartPolicy?.Name),
            Command = string.Join(" ", command),

            // The joined line above is for display; re-running this container needs the two lists
            // it was joined from — see ContainerInspect.Entrypoint.
            Entrypoint = r.Config?.Entrypoint is { } configEntrypoint ? [.. configEntrypoint] : [],
            Cmd = r.Config?.Cmd is { } configCmd ? [.. configCmd] : [],

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

    private Task PullCoreAsync(
        string reference, RegistryCredential? credential, IProgress<JSONMessage> progress, CancellationToken ct)
    {
        var (repo, tag) = SplitRepoTag(reference);
        return _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag },
            ToAuthConfig(credential), progress, ct);
    }

    /// <summary>
    /// The credential in the shape the engine API takes, or null to pull anonymously.
    /// <para>
    /// <c>ServerAddress</c> is sent as the host on its own. Docker matches it against the reference being
    /// pulled, and a scheme or a trailing path — which is how Hub logins are written in
    /// <c>config.json</c> — makes that match fail silently, falling back to an anonymous pull.
    /// </para>
    /// </summary>
    private static AuthConfig? ToAuthConfig(RegistryCredential? credential) =>
        credential is null
            ? null
            : new AuthConfig
            {
                ServerAddress = RegistryHost.Canonical(credential.Host),
                Username = credential.Username,
                Password = credential.Secret,
            };

    /// <summary>
    /// Asks the engine to authenticate against the registry — the daemon's <c>/auth</c> endpoint — so a
    /// login can be checked before it is stored.
    /// </summary>
    public ValueTask VerifyRegistryLoginAsync(RegistryCredential credential, CancellationToken ct = default) =>
        // A refusal is an exception from the daemon, which Exec turns into a Kontena error carrying the
        // registry's own message — usually clearer than anything this layer could write.
        Exec(() => _client.System.AuthenticateAsync(ToAuthConfig(credential), ct));

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
