using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Adapters.Docker;
using Kontena.Adapters.Kubernetes;
using Kontena.App.Services;
using Kontena.Core;
using Kontena.Core.Models;
using Kontena.Engines;

namespace Kontena.App.ViewModels;

/// <summary>Which page of the wizard is showing.</summary>
public enum AddBackendStep
{
    /// <summary>Step 1 — what is being connected to.</summary>
    What,

    /// <summary>Step 2 — an engine on another host.</summary>
    RemoteEngine,

    /// <summary>Step 2 — a kubeconfig and its contexts.</summary>
    Kubernetes,

    /// <summary>Step 3 — connecting, before anything is stored.</summary>
    Testing,

    /// <summary>Step 3 — it worked.</summary>
    Connected,

    /// <summary>Step 3 — it did not.</summary>
    Refused,
}

/// <summary>How one line of the connection test is doing.</summary>
public enum ProbeState { Waiting, Running, Done, Failed }

/// <summary>One line of the connection test — what is being tried, and how it went.</summary>
public partial class ProbeStepViewModel : ViewModelBase
{
    public ProbeStepViewModel(string text) => Text = text;

    public string Text { get; }

    [ObservableProperty] private ProbeState _state = ProbeState.Waiting;
    [ObservableProperty] private string? _elapsed;

    public bool IsWaiting => State == ProbeState.Waiting;
    public bool IsRunning => State == ProbeState.Running;
    public bool IsDone => State == ProbeState.Done;
    public bool IsFailed => State == ProbeState.Failed;

    partial void OnStateChanged(ProbeState value)
    {
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
    }
}

/// <summary>Something Kontena found by itself, shown so the user knows they need not add it.</summary>
/// <param name="Name">Engine or context name.</param>
/// <param name="Detail">Endpoint or source file.</param>
/// <param name="Chip">Switcher chip, so the row is recognisable.</param>
/// <param name="Connected">Whether it answered the last probe.</param>
public sealed record DetectedBackend(string Name, string Detail, string Chip, bool Connected)
{
    public string Status => Connected ? "in your switcher" : "not answering";
}

/// <summary>One context in the chosen kubeconfig, with whether the user wants it.</summary>
public partial class KubeContextChoice : ViewModelBase
{
    public KubeContextChoice(string name, string detail, bool selected)
    {
        Name = name;
        Detail = detail;
        _isSelected = selected;
    }

    public string Name { get; }
    public string Detail { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>Null until the reachability probe answers — "unknown" is not the same as "unreachable".</summary>
    [ObservableProperty] private bool? _reachable;

    /// <summary>True once this context is already a backend, so it cannot be added twice.</summary>
    [ObservableProperty] private bool _alreadyAdded;

    public bool IsProbing => Reachable is null && !AlreadyAdded;

    partial void OnReachableChanged(bool? value)
    {
        OnPropertyChanged(nameof(IsProbing));
        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnAlreadyAddedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProbing));
        OnPropertyChanged(nameof(StatusLabel));
    }

    public string StatusLabel => AlreadyAdded ? "already added"
        : Reachable switch { true => "reachable", false => "not reachable", _ => "checking…" };
}

/// <summary>
/// Adding an engine or a cluster, in three steps (KON-118).
/// <para>
/// The wizard exists because the old path let you store a connection and only find out afterwards
/// whether it worked. For a local socket that is harmless; for SSH, TLS certificates and kubeconfigs a
/// wrong field is the ordinary outcome, and the result was an entry in the switcher that did nothing.
/// So the last step is the connection itself, and nothing is written before it answers.
/// </para>
/// <para>
/// Adding only. Changing and removing stay in Settings › Engines — two places that both half-manage the
/// same list is how they drift apart.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is torn down by Close, which every exit from the dialog goes "
        + "through. Making the view model IDisposable would hand the shell a second way to end it.")]
public partial class AddBackendViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly Action _onClose;
    private readonly Func<string?, Task> _onAdded;
    private readonly IReadOnlyList<BackendProbe> _probes;

    private RemoteEngine? _verified;
    private List<string> _verifiedContexts = [];
    private CancellationTokenSource? _test;

    /// <param name="store">Settings, written only after a successful test.</param>
    /// <param name="probes">What the shell already knows about, for the detected list.</param>
    /// <param name="onClose">Dismisses the dialog.</param>
    /// <param name="onAdded">
    /// Rebuilds the backend list, and switches to the given backend id when one is passed.
    /// </param>
    public AddBackendViewModel(
        SettingsStore store,
        IReadOnlyList<BackendProbe> probes,
        Action onClose,
        Func<string?, Task> onAdded)
    {
        _store = store;
        _probes = probes;
        _onClose = onClose;
        _onAdded = onAdded;

        KubeconfigPath = Kubeconfig.DefaultPath;
        LoadDetected();
    }

    // ── Step ────────────────────────────────────────────────────────────────

    [ObservableProperty] private AddBackendStep _step = AddBackendStep.What;

    partial void OnStepChanged(AddBackendStep value)
    {
        foreach (var name in new[]
        {
            nameof(IsWhat), nameof(IsRemoteEngine), nameof(IsKubernetes), nameof(IsTesting),
            nameof(IsConnected), nameof(IsRefused), nameof(Title), nameof(Subtitle), nameof(StepLabel),
            nameof(PrimaryLabel), nameof(CanGoBack), nameof(ShowPrimaryArrow), nameof(IsStepTwoActive),
            nameof(IsStepThreeActive), nameof(IsStepOneDone), nameof(IsStepTwoDone), nameof(CanContinue),
        })
        {
            OnPropertyChanged(name);
        }
    }

    public bool IsWhat => Step == AddBackendStep.What;
    public bool IsRemoteEngine => Step == AddBackendStep.RemoteEngine;
    public bool IsKubernetes => Step == AddBackendStep.Kubernetes;
    public bool IsTesting => Step == AddBackendStep.Testing;
    public bool IsConnected => Step == AddBackendStep.Connected;
    public bool IsRefused => Step == AddBackendStep.Refused;

    private int StepNumber => Step switch
    {
        AddBackendStep.What => 1,
        AddBackendStep.RemoteEngine or AddBackendStep.Kubernetes => 2,
        _ => 3,
    };

    public bool IsStepOneDone => StepNumber > 1;
    public bool IsStepTwoDone => StepNumber > 2;
    public bool IsStepTwoActive => StepNumber >= 2;
    public bool IsStepThreeActive => StepNumber >= 3;

    public string StepLabel => Step switch
    {
        AddBackendStep.What => "Step 1 of 3 · What",
        AddBackendStep.RemoteEngine or AddBackendStep.Kubernetes => "Step 2 of 3 · Where",
        _ => "Step 3 of 3 · Test",
    };

    public string Title => Step switch
    {
        AddBackendStep.What => "Add engine or cluster",
        AddBackendStep.RemoteEngine => "Container engine on another host",
        AddBackendStep.Kubernetes => "Kubernetes cluster",
        AddBackendStep.Testing => "Testing the connection",
        AddBackendStep.Connected => "Connection works",
        _ => "Could not connect",
    };

    public string Subtitle => Step switch
    {
        AddBackendStep.What =>
            "Kontena talks to container engines and Kubernetes clusters. Pick what you are connecting to.",
        AddBackendStep.RemoteEngine =>
            "It appears in the switcher like a local one — same pages, same actions.",
        AddBackendStep.Kubernetes => "Pick the contexts you want in the switcher.",
        AddBackendStep.Testing => "Kontena is checking this works before it stores anything.",
        AddBackendStep.Connected => "Give it a name and it goes into the switcher.",
        _ => "Nothing was stored.",
    };

    public string PrimaryLabel => Step switch
    {
        AddBackendStep.What => "Continue",
        AddBackendStep.RemoteEngine => "Test connection",
        AddBackendStep.Kubernetes => SelectedContextCount == 1
            ? "Test 1 context"
            : $"Test {SelectedContextCount} contexts",
        AddBackendStep.Testing => "Cancel test",
        AddBackendStep.Connected => "Add and switch to it",
        _ => "Try again",
    };

    /// <summary>Only the button that moves a step forward carries the arrow.</summary>
    public bool ShowPrimaryArrow => Step is not (AddBackendStep.Testing or AddBackendStep.Connected);

    public bool CanGoBack => Step != AddBackendStep.What;

    // ── Step 1 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What Kontena found by itself. Listed without an "add" button on purpose: local engines and the
    /// contexts in the default kubeconfig are discovered and are already in the switcher. A button
    /// promising to add what is present would be the third dead control this week.
    /// </summary>
    public ObservableCollection<DetectedBackend> Detected { get; } = [];

    public bool HasDetected => Detected.Count > 0;

    private void LoadDetected()
    {
        Detected.Clear();
        foreach (var probe in _probes)
        {
            // Remote engines are the user's own entries, not something found on this machine.
            if (probe.Provider.Backend.StartsWith("docker-remote:", StringComparison.Ordinal))
                continue;

            Detected.Add(new DetectedBackend(
                probe.Provider.DisplayName,
                probe.Detail ?? string.Empty,
                probe.Provider.Chip,
                probe.Connected));
        }

        OnPropertyChanged(nameof(HasDetected));
    }

    [RelayCommand]
    private void ChooseRemoteEngine() => Step = AddBackendStep.RemoteEngine;

    [RelayCommand]
    private void ChooseKubernetes()
    {
        Step = AddBackendStep.Kubernetes;
        LoadContexts();
    }

    // ── Step 2 · remote engine ──────────────────────────────────────────────

    [ObservableProperty] private bool _isSsh = true;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = string.Empty;
    [ObservableProperty] private string _user = string.Empty;
    [ObservableProperty] private string _socketPath = string.Empty;
    [ObservableProperty] private string _certificateDirectory = string.Empty;
    [ObservableProperty] private bool _allowInsecure;
    [ObservableProperty] private string _name = string.Empty;

    public bool IsTcp => !IsSsh;

    /// <summary>Once the endpoint is declared insecure the certificate fields are not a choice any more.</summary>
    public bool CertificatesApply => IsTcp && !AllowInsecure;

    public string PortPlaceholder => IsSsh ? "22" : "2376";

    public RemoteEngineDraft Draft => new()
    {
        Name = Name,
        Host = Host,
        User = User,
        Port = Port,
        SocketPath = SocketPath,
        CertificateDirectory = CertificateDirectory,
        AllowInsecure = AllowInsecure,
        IsSsh = IsSsh,
    };

    /// <summary>Shown for TCP with no certificates given, which is where the decision is actually made.</summary>
    public bool ShowInsecureWarning => AllowInsecure && IsTcp;

    [RelayCommand]
    private void SetTransport(string transport) => IsSsh = transport != "tcp";

    partial void OnIsSshChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTcp));
        OnPropertyChanged(nameof(CertificatesApply));
        OnPropertyChanged(nameof(PortPlaceholder));
        OnPropertyChanged(nameof(ShowInsecureWarning));
        OnFormChanged();
    }

    partial void OnAllowInsecureChanged(bool value)
    {
        OnPropertyChanged(nameof(CertificatesApply));
        OnPropertyChanged(nameof(ShowInsecureWarning));
        OnFormChanged();
    }

    partial void OnHostChanged(string value) => OnFormChanged();
    partial void OnPortChanged(string value) => OnFormChanged();
    partial void OnCertificateDirectoryChanged(string value) => OnFormChanged();

    private void OnFormChanged()
    {
        Error = null;
        OnPropertyChanged(nameof(CanContinue));
    }

    // ── Step 2 · Kubernetes ─────────────────────────────────────────────────

    [ObservableProperty] private string _kubeconfigPath = string.Empty;
    [ObservableProperty] private string? _kubeconfigProblem;

    public ObservableCollection<KubeContextChoice> Contexts { get; } = [];

    public bool HasContexts => Contexts.Count > 0;

    public int SelectedContextCount => Contexts.Count(c => c.IsSelected && !c.AlreadyAdded);

    partial void OnKubeconfigPathChanged(string value) => LoadContexts();

    /// <summary>
    /// Reads the contexts out of the chosen file, then checks reachability in the background. The list
    /// appears at once because a probe against an unreachable apiserver takes its timeout, and a wizard
    /// that shows nothing for ten seconds looks broken.
    /// </summary>
    private void LoadContexts()
    {
        Contexts.Clear();
        KubeconfigProblem = null;

        var path = KubeconfigPath.Trim();
        if (path.Length == 0)
        {
            KubeconfigProblem = "Give the path to a kubeconfig file.";
            AfterContextsChanged();
            return;
        }

        if (!File.Exists(Kubeconfig.Expand(path)))
        {
            KubeconfigProblem = $"No file at {path}.";
            AfterContextsChanged();
            return;
        }

        var isDefault = string.Equals(
            Kubeconfig.Expand(path), Kubeconfig.Expand(Kubeconfig.DefaultPath), StringComparison.Ordinal);

        var contexts = Kubeconfig.LoadContexts(isDefault ? null : path);
        if (contexts.Count == 0)
        {
            KubeconfigProblem = "That file holds no contexts, or could not be parsed as a kubeconfig.";
            AfterContextsChanged();
            return;
        }

        foreach (var context in contexts)
        {
            var backend = new KubernetesClusterProvider(context.Name, isDefault ? null : path).Backend;
            var known = _probes.Any(p => p.Provider.Backend == backend);

            var detail = string.IsNullOrEmpty(context.Namespace)
                ? context.Cluster
                : $"{context.Cluster} · namespace: {context.Namespace}";

            var choice = new KubeContextChoice(context.Name, detail, selected: !known)
            {
                AlreadyAdded = known,
            };

            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(KubeContextChoice.IsSelected))
                    AfterContextsChanged();
            };

            Contexts.Add(choice);
        }

        AfterContextsChanged();
        _ = ProbeContextsAsync(isDefault ? null : path);
    }

    private void AfterContextsChanged()
    {
        OnPropertyChanged(nameof(HasContexts));
        OnPropertyChanged(nameof(SelectedContextCount));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(CanContinue));
    }

    private async Task ProbeContextsAsync(string? path)
    {
        foreach (var choice in Contexts.ToList())
        {
            if (choice.AlreadyAdded)
                continue;

            var name = choice.Name;
            var reachable = await Task.Run(async () =>
            {
                try
                {
                    var engine = new KubernetesClusterEngine(name, path);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    await engine.PingAsync(cts.Token);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            });

            choice.Reachable = reachable;
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    [ObservableProperty] private string? _error;

    public bool CanContinue => Step switch
    {
        AddBackendStep.What => true,
        AddBackendStep.RemoteEngine => Draft.Problem is null,
        AddBackendStep.Kubernetes => SelectedContextCount > 0,
        _ => true,
    };

    [RelayCommand]
    private void Back()
    {
        switch (Step)
        {
            case AddBackendStep.RemoteEngine:
            case AddBackendStep.Kubernetes:
                Step = AddBackendStep.What;
                break;

            case AddBackendStep.Testing:
                CancelTest();
                break;

            default:
                // From a result, back to the form that produced it.
                Step = _verifiedContexts.Count > 0 || Contexts.Count > 0
                    ? AddBackendStep.Kubernetes
                    : AddBackendStep.RemoteEngine;
                break;
        }
    }

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        switch (Step)
        {
            case AddBackendStep.What:
                Step = AddBackendStep.RemoteEngine;
                break;

            case AddBackendStep.RemoteEngine:
                await TestRemoteAsync();
                break;

            case AddBackendStep.Kubernetes:
                await TestContextsAsync();
                break;

            case AddBackendStep.Testing:
                CancelTest();
                break;

            case AddBackendStep.Connected:
                await CommitAsync();
                break;

            default:
                Step = _verifiedContexts.Count > 0 || Contexts.Count > 0
                    ? AddBackendStep.Kubernetes
                    : AddBackendStep.RemoteEngine;
                break;
        }
    }

    private void CancelTest()
    {
        _test?.Cancel();
        Step = Contexts.Count > 0 ? AddBackendStep.Kubernetes : AddBackendStep.RemoteEngine;
    }

    [RelayCommand]
    private void Close()
    {
        _test?.Cancel();
        _test?.Dispose();
        _test = null;
        _onClose();
    }

    // ── Step 3 ──────────────────────────────────────────────────────────────

    public ObservableCollection<ProbeStepViewModel> ProbeSteps { get; } = [];

    [ObservableProperty] private string _testTarget = string.Empty;

    /// <summary>What the engine or cluster said about itself once it answered.</summary>
    public ObservableCollection<string> Facts { get; } = [];

    [ObservableProperty] private string _successHeadline = string.Empty;
    [ObservableProperty] private string _failureHeadline = string.Empty;
    [ObservableProperty] private string _failureExplanation = string.Empty;
    [ObservableProperty] private string _failureOutput = string.Empty;

    /// <summary>Concrete things to check, chosen for the failure that actually happened.</summary>
    public ObservableCollection<string> FailureHints { get; } = [];

    private async Task TestRemoteAsync()
    {
        var draft = Draft;
        if (draft.Problem is { } problem)
        {
            Error = problem;
            return;
        }

        var remote = draft.Build();
        TestTarget = remote.Endpoint;

        var resolve = new ProbeStepViewModel("Host resolved");
        var connect = new ProbeStepViewModel(
            remote.Transport == RemoteEngineTransport.Ssh ? "SSH tunnel opened" : "TLS connection accepted");
        var version = new ProbeStepViewModel("Asking the engine for its version");
        var inventory = new ProbeStepViewModel("Reading containers and images");

        ProbeSteps.Clear();
        foreach (var s in new[] { resolve, connect, version, inventory })
            ProbeSteps.Add(s);

        Step = AddBackendStep.Testing;

        _test?.Dispose();
        _test = new CancellationTokenSource();
        var ct = _test.Token;

        try
        {
            await RunStepAsync(resolve, () => Dns.GetHostAddressesAsync(remote.Host, ct), ct);

            IBackend? backend = null;
            try
            {
                await RunStepAsync(connect, async () =>
                {
                    // Creating the backend is what opens the tunnel, so it belongs to this step.
                    backend = await Task.Run(() => new RemoteDockerEngineProvider(remote).CreateBackend(), ct);
                    await backend.PingAsync(ct);
                }, ct);

                BackendInfo? info = null;
                await RunStepAsync(version, async () => info = await backend!.GetInfoAsync(ct), ct);

                var containers = 0;
                var images = 0;
                var volumes = 0;
                await RunStepAsync(inventory, async () =>
                {
                    if (backend is IContainerEngine engine)
                    {
                        containers = (await engine.ListContainersAsync(all: true, ct)).Count;
                        images = (await engine.ListImagesAsync(ct)).Count;
                        volumes = (await engine.ListVolumesAsync(ct)).Count;
                    }
                }, ct);

                Facts.Clear();
                if (info is not null)
                {
                    Facts.Add($"{info.DisplayName} {info.Version}".Trim());
                    if (!string.IsNullOrEmpty(info.Endpoint))
                        Facts.Add(info.Endpoint);
                }

                Facts.Add(Plural(containers, "container"));
                Facts.Add(Plural(images, "image"));
                Facts.Add(Plural(volumes, "volume"));

                SuccessHeadline = $"Connected to {remote.Host}";
                _verified = remote;
                _verifiedContexts = [];
                if (string.IsNullOrWhiteSpace(Name))
                    Name = remote.Host;

                Step = AddBackendStep.Connected;
            }
            finally
            {
                // A test must not leave a tunnel behind: disposing takes the ssh process with it.
                (backend as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // The user cancelled; CancelTest already moved back to the form.
        }
        catch (Exception ex)
        {
            Fail(remote, ex);
        }
    }

    private async Task TestContextsAsync()
    {
        var path = KubeconfigPath.Trim();
        var isDefault = string.Equals(
            Kubeconfig.Expand(path), Kubeconfig.Expand(Kubeconfig.DefaultPath), StringComparison.Ordinal);

        var wanted = Contexts.Where(c => c.IsSelected && !c.AlreadyAdded).Select(c => c.Name).ToList();
        if (wanted.Count == 0)
            return;

        TestTarget = path;
        ProbeSteps.Clear();
        var steps = wanted.ToDictionary(n => n, n => new ProbeStepViewModel($"Connecting to {n}"));
        foreach (var s in steps.Values)
            ProbeSteps.Add(s);

        Step = AddBackendStep.Testing;

        _test?.Dispose();
        _test = new CancellationTokenSource();
        var ct = _test.Token;

        var reached = new List<string>();
        string? lastError = null;

        try
        {
            foreach (var context in wanted)
            {
                try
                {
                    await RunStepAsync(steps[context], async () =>
                    {
                        var engine = new KubernetesClusterEngine(context, isDefault ? null : path);
                        await engine.PingAsync(ct);
                    }, ct);

                    reached.Add(context);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One unreachable cluster does not invalidate the others in the same file.
                    lastError = ex.Message;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (reached.Count == 0)
        {
            FailureHeadline = "No cluster answered";
            FailureExplanation = wanted.Count == 1
                ? "The context is in the file, but its apiserver did not respond."
                : "None of the selected contexts responded.";
            FailureOutput = lastError ?? string.Empty;
            FailureHints.Clear();
            FailureHints.Add("Check the cluster is running and reachable from this machine");
            FailureHints.Add("Check the credentials in the kubeconfig have not expired");
            FailureHints.Add($"Try it yourself: kubectl --kubeconfig {path} get nodes");
            Step = AddBackendStep.Refused;
            return;
        }

        Facts.Clear();
        Facts.Add(reached.Count == 1 ? "1 cluster" : $"{reached.Count} clusters");
        foreach (var context in reached)
            Facts.Add(context);

        SuccessHeadline = reached.Count == wanted.Count
            ? $"Connected to {(reached.Count == 1 ? "the cluster" : "all selected clusters")}"
            : $"Connected to {reached.Count} of {wanted.Count} clusters";

        _verified = null;
        _verifiedContexts = reached;
        Name = path;
        Step = AddBackendStep.Connected;
    }

    /// <summary>Runs one line of the test, timing it and marking how it ended.</summary>
    private static async Task RunStepAsync(ProbeStepViewModel step, Func<Task> work, CancellationToken ct)
    {
        step.State = ProbeState.Running;
        var watch = Stopwatch.StartNew();
        try
        {
            await work();
            step.Elapsed = $"{watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
            step.State = ProbeState.Done;
        }
        catch (Exception)
        {
            step.Elapsed = null;
            step.State = ProbeState.Failed;
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private void Fail(RemoteEngine remote, Exception ex)
    {
        // The transport's own words. "Permission denied (publickey)" and "Host key verification failed"
        // name the thing to fix, and nothing written here would name it better.
        FailureOutput = ex.Message.Trim();
        FailureHints.Clear();

        var target = remote.User is { Length: > 0 } u ? $"{u}@{remote.Host}" : remote.Host;
        var message = ex.Message;

        if (remote.Transport == RemoteEngineTransport.Ssh
            && message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            FailureHeadline = "ssh refused the connection";
            FailureExplanation = "The host answered, so the address is right — it would not let this key in.";
            FailureHints.Add($"Check the key is on the host: ssh-copy-id {target}");
            FailureHints.Add("Check your agent is running: ssh-add -l");
            FailureHints.Add($"Try it yourself: ssh {target} — if that fails, Kontena cannot help");
        }
        else if (message.Contains("Host key verification", StringComparison.OrdinalIgnoreCase))
        {
            FailureHeadline = "The host key is not known";
            FailureExplanation =
                "ssh will not connect to a host it has never seen. Kontena does not accept keys on your "
                + "behalf: that decision is the point of the check.";
            FailureHints.Add($"Connect once by hand and accept the key: ssh {target}");
        }
        else if (remote.Transport == RemoteEngineTransport.Tcp)
        {
            FailureHeadline = "The engine did not accept the connection";
            FailureExplanation = remote.CertificateDirectory is null
                ? "The port did not answer, or it is not an engine."
                : "The port answered but the TLS exchange failed.";
            FailureHints.Add($"Check the engine listens on {remote.Endpoint}");
            if (remote.CertificateDirectory is { Length: > 0 } dir)
            {
                FailureHints.Add($"Check ca.pem, cert.pem and key.pem are in {dir}");
                FailureHints.Add($"Try it yourself: docker --tlsverify --tlscacert {dir}/ca.pem -H {remote.Endpoint} version");
            }
        }
        else
        {
            FailureHeadline = "Could not reach the engine";
            FailureExplanation = "The connection did not complete.";
            FailureHints.Add($"Try it yourself: ssh {target}");
        }

        Step = AddBackendStep.Refused;
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ── Commit ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores what the test proved, and nothing else. Runs only from the Connected step, so there is no
    /// path that writes an untested connection.
    /// </summary>
    private async Task CommitAsync()
    {
        string? switchTo = null;

        if (_verified is { } remote)
        {
            var named = remote with
            {
                Name = string.IsNullOrWhiteSpace(Name) ? remote.Host : Name.Trim(),
            };

            _store.Update(s => s with { RemoteEngines = [.. s.RemoteEngines, named] });
            switchTo = named.Backend;
        }
        else if (_verifiedContexts.Count > 0)
        {
            var path = KubeconfigPath.Trim();
            var isDefault = string.Equals(
                Kubeconfig.Expand(path), Kubeconfig.Expand(Kubeconfig.DefaultPath), StringComparison.Ordinal);

            // The default kubeconfig is always read, so remembering it would be a duplicate entry.
            if (!isDefault)
            {
                _store.Update(s => s.KubeconfigPaths.Contains(path, StringComparer.Ordinal)
                    ? s
                    : s with { KubeconfigPaths = [.. s.KubeconfigPaths, path] });
            }

            switchTo = new KubernetesClusterProvider(_verifiedContexts[0], isDefault ? null : path).Backend;
        }

        _onClose();
        await _onAdded(switchTo);
    }
}
