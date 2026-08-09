using Kontena.Sdk.Models;
namespace Kontena.Core.Models;

/// <summary>How Kontena picks its light/dark appearance.</summary>
public enum ThemePreference
{
    /// <summary>Follow the operating system.</summary>
    System = 0,
    Light,
    Dark,
}

/// <summary>
/// Which stream of releases Kontena offers to update to. One per stream the Build workflow
/// publishes, so every feed that exists can be subscribed to.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Tagged releases only — what almost everyone wants.</summary>
    Stable = 0,

    /// <summary>The rolling prerelease from <c>main</c>: what is promoted, before it is tagged.</summary>
    Preview,

    /// <summary>The rolling nightly prerelease cut from <c>develop</c> (KON-108).</summary>
    Nightly,
}

/// <summary>What Kontena connects to when it starts.</summary>
public enum StartupBackend
{
    /// <summary>Whatever was open last — the default, and what most people mean.</summary>
    LastUsed = 0,

    /// <summary>One named backend, every time, whatever was open last.</summary>
    Pinned,

    /// <summary>The first container engine that answers.</summary>
    FirstConnected,
}

/// <summary>
/// User-facing application settings, persisted between launches. Kept engine- and
/// UI-framework-neutral so it can live in Core and be round-tripped in tests.
/// </summary>
public sealed record KontenaSettings
{
    /// <summary>Light/dark/system appearance.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.Dark;

    /// <summary>Tighter rows in the container/image/volume lists.</summary>
    public bool CompactDensity { get; init; }

    /// <summary>Continuously watch for engines starting/stopping.</summary>
    public bool AutoDetectEngines { get; init; } = true;

    /// <summary>
    /// Legacy: the engine to activate on launch, from before clusters existed and before "last
    /// used" was an option. Superseded by <see cref="Startup"/> and <see cref="PinnedBackend"/>,
    /// and only read now to carry an existing choice forward — see <see cref="ResolvedStartup"/>.
    /// </summary>
    public string? DefaultEngine { get; init; }

    /// <summary>
    /// How the launch backend is chosen. Nullable on purpose: null means the file predates this
    /// setting, and <see cref="ResolvedStartup"/> reads the old <see cref="DefaultEngine"/> instead
    /// — a stored preference should survive an upgrade, not be silently replaced by a default.
    /// </summary>
    public StartupBackend? Startup { get; init; }

    /// <summary>The backend <see cref="StartupBackend.Pinned"/> refers to; a full backend id.</summary>
    public string? PinnedBackend { get; init; }

    /// <summary>
    /// The last backend that connected — a full id, so <c>kubernetes:kind-kind</c> as readily as
    /// <c>docker</c>. Written on every successful activation; it records behaviour, where
    /// <see cref="PinnedBackend"/> records a choice.
    /// </summary>
    public string? LastBackend { get; init; }

    /// <summary>The effective startup mode, honouring a pre-upgrade <see cref="DefaultEngine"/>.</summary>
    public StartupBackend ResolvedStartup => Startup
        ?? (string.IsNullOrEmpty(DefaultEngine) ? StartupBackend.LastUsed : StartupBackend.Pinned);

    /// <summary>The effective pinned backend, honouring a pre-upgrade <see cref="DefaultEngine"/>.</summary>
    public string? ResolvedPinnedBackend =>
        string.IsNullOrEmpty(PinnedBackend) ? DefaultEngine : PinnedBackend;

    /// <summary>
    /// The backend to try first on launch, or null when nothing is remembered or pinned and the
    /// first engine that answers should win.
    /// </summary>
    public string? StartupTarget => ResolvedStartup switch
    {
        StartupBackend.Pinned => NullIfEmpty(ResolvedPinnedBackend),
        StartupBackend.LastUsed => NullIfEmpty(LastBackend),
        _ => null,
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Whether the in-memory demo backends (fake engine and fake clusters) appear in the switcher.
    /// <para>
    /// Nullable on purpose: null means "use the build default" — on for a debug build, off for a
    /// release build. A settings file written during development therefore never switches demo
    /// backends on in a release build, which an ordinary bool default could not express.
    /// </para>
    /// </summary>
    public bool? ShowDemoBackends { get; init; }

    /// <summary>Whether the first-run onboarding (engine connect) has been completed.</summary>
    public bool Onboarded { get; init; }

    /// <summary>Start Kontena at login (stored preference; wiring is platform-specific).</summary>
    public bool LaunchAtLogin { get; init; }

    // ── Updates (KON-110) ─────────────────────────────────────────────────────

    /// <summary>
    /// Which release stream to offer updates from, or null when the user has never chosen (KON-123).
    /// <para>
    /// Nullable for the same reason as <see cref="ShowDemoBackends"/>: "not chosen" and "chose Stable"
    /// are different answers. Someone who deliberately downloaded a nightly has already made the choice
    /// by downloading it, and offering them a move to stable on first launch overrules it.
    /// </para>
    /// <para>
    /// A stored choice always wins — see <see cref="ResolvedUpdateChannel"/>. That is what keeps the
    /// rule from KON-110 intact: an install never drifts onto a rolling stream by itself.
    /// </para>
    /// </summary>
    public UpdateChannel? UpdateChannel { get; init; }

    /// <summary>
    /// The channel to actually read: what the user chose, or failing that the stream this build came
    /// from.
    /// </summary>
    /// <param name="buildChannel">
    /// The running build's own stream, from <see cref="ReleaseChannel.FromVersion"/>.
    /// </param>
    public UpdateChannel ResolvedUpdateChannel(UpdateChannel buildChannel) =>
        UpdateChannel ?? buildChannel;

    /// <summary>
    /// Fetch a found update straight away instead of waiting for the user to ask. On by default:
    /// the update card offers a restart rather than a download, which is the shorter path, and the
    /// download is idle-time work either way. Turning it off keeps the check but not the transfer.
    /// </summary>
    public bool AutoDownloadUpdates { get; init; } = true;

    /// <summary>
    /// The version the user last chose to skip the toast for. A toast for the same version on every
    /// launch is nagging; the sidebar entry stays either way, so the update is never hidden.
    /// </summary>
    public string? DismissedUpdateVersion { get; init; }

    /// <summary>
    /// Registries Kontena has a login for, without the secrets — those live in the OS keychain, keyed by
    /// host (KON-114). Logins found in the engine's own config are not listed here: they are read live,
    /// because they are not ours to remember or to go stale.
    /// </summary>
    public IReadOnlyList<RegistryLogin> Registries { get; init; } = [];

    /// <summary>
    /// Engines on other hosts, added by the user (KON-46). Nothing secret is in here — an SSH key
    /// passphrase or a certificate password belongs in the keychain, keyed by the remote's id.
    /// </summary>
    public IReadOnlyList<RemoteEngine> RemoteEngines { get; init; } = [];

    /// <summary>
    /// Kubeconfig files beyond the default one, added by the user (KON-118). The default
    /// (<c>$KUBECONFIG</c>, else <c>~/.kube/config</c>) is always read and is never listed here — a
    /// downloaded cluster config that lives somewhere else has no other way to be found.
    /// <para>
    /// Paths only. Kontena reads these files and never writes to them.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> KubeconfigPaths { get; init; } = [];

    /// <summary>
    /// Plugins the user has agreed to run, as <c>"&lt;id&gt;@&lt;version&gt;"</c>. Until releases are
    /// signed (KON-53), a dll in the plugins directory is arbitrary code in Kontena's process, so
    /// nothing loads without an answer here.
    /// <para>
    /// Per id <b>and</b> version on purpose: an update is different bytes, and the permission was given
    /// for the old ones. It is a weak boundary — a hostile replacement can lie about its own version to
    /// reuse an answer — and that is exactly the hole the signature check closes. It is not worked
    /// around here, because a workaround would only look like a boundary.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedPlugins { get; init; } = [];

    /// <summary>Whether this exact build of this plugin has been agreed to.</summary>
    public bool AllowsPlugin(string id, string version) =>
        AllowedPlugins.Contains(PluginKey(id, version), StringComparer.Ordinal);

    /// <summary>Record agreement for this exact build, leaving earlier versions recorded.</summary>
    public KontenaSettings WithAllowedPlugin(string id, string version) =>
        AllowsPlugin(id, version)
            ? this
            : this with { AllowedPlugins = [.. AllowedPlugins, PluginKey(id, version)] };

    private static string PluginKey(string id, string version) => $"{id}@{version}";

    /// <summary>
    /// Names the user gave a backend, keyed by backend id (KON-119). Empty means "use what the source
    /// calls itself".
    /// <para>
    /// A source's own name is not always meant to be read in a list — a kube-context is routinely
    /// <c>gke_myproject-prod_europe-west4_cluster-1</c>, and that name comes from the cluster's owner
    /// rather than from the person looking at the switcher.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> BackendNames { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Every cluster Kontena has seen, and whether it belongs in the switcher (KON-120).
    /// <para>
    /// Local engines are discovered and added; clusters are discovered and offered. A kubeconfig is a
    /// collection that accumulates — old customers, dead kind clusters, and production — and listing all
    /// of it puts a production cluster one click from a toy.
    /// </para>
    /// <para>
    /// A context that was seen and declined stays here as <c>false</c>, so it is not offered again every
    /// launch. Empty on an installation that predates this, which is what triggers the one-time adoption
    /// of everything already visible.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, bool> KnownClusters { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>
    /// Whether the Containers list groups Compose projects into one row, per backend id (KON-159).
    /// <para>
    /// Per backend rather than global: a machine full of stacks and one with none are different rooms,
    /// and the answer that suits a laptop running four compose files is not the one that suits a
    /// cluster node. Absent means on — grouping is the default, and a flat list is the opt-out.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, bool> ContainerGrouping { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>
    /// Keyboard shortcuts the user changed, keyed by action id (KON-180). Absent means "use the
    /// default", following <see cref="BackendNames"/> and <see cref="ContainerGrouping"/>.
    /// <para>
    /// Only deviations, deliberately. Writing the full set out would freeze today's defaults into every
    /// installation, so a shortcut improved in a later release would reach nobody who had ever opened
    /// this page — and the defaults differ per platform, which a stored copy cannot follow.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Shortcuts { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Primary monospace family for the container terminal.</summary>
    public string TerminalFontFamily { get; init; } = "JetBrains Mono";

    /// <summary>Terminal font size in points.</summary>
    public double TerminalFontSize { get; init; } = 12;

    /// <summary>Enable programming-font ligatures in the terminal.</summary>
    public bool TerminalLigatures { get; init; } = true;


    /// <summary>
    /// How wide the detail drawer is, in layout pixels (KON-307). Dragged rather than chosen in
    /// Settings: how much of the list you want to keep in view depends on the list, and the answer
    /// changes between a node grid and a workload table.
    /// </summary>
    public double DetailDrawerWidth { get; init; } = 500;

    /// <summary>Recently used build-context folders, most-recent first (for the Build modal).</summary>
    public IReadOnlyList<string> RecentBuildContexts { get; init; } = [];

    /// <summary>
    /// Port forwards worth offering again on the next visit, keyed by the full backend id they were
    /// opened against (<c>kubernetes:kind-kind</c>) — a tunnel means nothing on another cluster.
    /// The tunnels themselves cannot be persisted; this is the intent behind them (KON-105).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RememberedPortForward>> PortForwards { get; init; } =
        new Dictionary<string, IReadOnlyList<RememberedPortForward>>();

    // ── Window placement (restored on launch) ─────────────────────────────────

    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public bool WindowMaximized { get; init; }
}

/// <summary>Terminal font settings resolved for a session (family carries a mono fallback).</summary>
public sealed record TerminalFont(string Family, double Size, bool Ligatures);

/// <summary>
/// What the pod-detail usage charts can and cannot reach (KON-345).
/// <para>
/// No placement setting: the sparkline in the header and the Metrics tab are both always there.
/// They answer different questions — one is a glance, the other is where you go to dig — so making
/// them alternatives only forced a choice between two things you want at once.
/// </para>
/// </summary>
public static class UsageGraphs
{
    /// <summary>
    /// The furthest back the in-session buffer is kept. Not a display choice: sampling every 15s,
    /// an hour of history is 240 points held per open pod for a chart nobody asked to see, and
    /// anything past the buffer belongs to a real history source anyway (KON-84).
    /// </summary>
    public static readonly TimeSpan LiveBuffer = TimeSpan.FromMinutes(15);

    /// <summary>Ranges the range selector offers, in minutes.</summary>
    public static readonly IReadOnlyList<int> Ranges = [5, 15, 60, 360, 1440, 10080];

    /// <summary>What a pod opens on — the whole buffer, so nothing sampled is hidden by default.</summary>
    public const int DefaultRangeMinutes = 15;

    /// <summary>Whether a range is reachable from the live buffer alone.</summary>
    public static bool IsLive(int minutes) => minutes <= LiveBuffer.TotalMinutes;

    /// <summary>The range to actually chart — never further back than the buffer can answer.</summary>
    public static TimeSpan Range(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, 1, (int)LiveBuffer.TotalMinutes));
}

/// <summary>
/// One port forward as it is remembered between launches: what it pointed at and which ports it
/// used, which is everything needed to open it again. The resource coordinate is carried as plain
/// strings so this stays in Core, next to the rest of the settings, without Core learning what a
/// Kubernetes kind is.
/// </summary>
/// <param name="Group">API group of the target; empty for the core group (Pod, Service).</param>
/// <param name="Version">API version of the target.</param>
/// <param name="Kind">Resource kind of the target.</param>
/// <param name="Namespace">Namespace, or null for a cluster-scoped target.</param>
/// <param name="Name">Resource name.</param>
/// <param name="Label">How the target was labelled in the UI ("name · namespace").</param>
/// <param name="RemotePort">The port on the pod/service.</param>
/// <param name="LocalPort">The local port it listened on — the address you handed to other things.</param>
public sealed record RememberedPortForward(
    string Group, string Version, string Kind, string? Namespace, string Name,
    string Label, int RemotePort, int LocalPort);
