using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration.Provisioning;
using Kontena.Sdk.Orchestration.Provisioning;
using Kontena.Sdk.Tooling;

namespace Kontena.App.ViewModels;

/// <summary>Which of the three screens the rollout is showing.</summary>
public enum RolloutStage
{
    /// <summary>Not started, or an interrupted one waiting to be resumed.</summary>
    Idle,

    /// <summary>Running. Rows per machine, and the tool's own console.</summary>
    Running,

    /// <summary>Stopped on a machine. Three ways on, and nothing undone.</summary>
    Failed,

    /// <summary>Every machine up.</summary>
    Done,
}

/// <summary>
/// Running the rollout, and what is on screen when it stops (KON-239).
/// <para>
/// The failure path is in the same class as the action for the same reason removing a cluster lives
/// next to adding one (KON-76): it is the other half of the same thing, and a half-built cluster on
/// five machines is exactly the moment someone needs the screen to be good.
/// </para>
/// <para>
/// <b>Nothing is rolled back.</b> Undoing a half-finished rollout would take out the machines that did
/// work, which is a worse position than the one being complained about — so the screen says so out
/// loud. Without that sentence, the absence of a rollback button reads as something we forgot.
/// </para>
/// </summary>
public sealed partial class RolloutViewModel : ViewModelBase
{
    private readonly IRemoteClusterProvisioner _provisioner;
    private readonly RolloutRecordStore _records;
    private CancellationTokenSource? _running;
    private RolloutTracker? _tracker;

    public RolloutViewModel(IRemoteClusterProvisioner provisioner, RolloutRecordStore? records = null)
    {
        ArgumentNullException.ThrowIfNull(provisioner);

        _provisioner = provisioner;
        _records = records ?? new RolloutRecordStore();
        Interrupted = _records.Read();
    }

    /// <summary>The tool's own output, verbatim. Not our summary of it — the same call the local
    /// create screen made, and for the same reason: a paraphrase is the first thing to be wrong.</summary>
    public ObservableCollection<string> Output { get; } = [];

    /// <summary>One row per machine, read off that output as it arrives.</summary>
    public ObservableCollection<RolloutHostRowViewModel> Rows { get; } = [];

    [ObservableProperty] private RolloutStage _stage = RolloutStage.Idle;

    [ObservableProperty] private string _clusterName = string.Empty;

    /// <summary>The tool's complaint, when it made one.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>A rollout that was interrupted by the app closing, or null.</summary>
    [ObservableProperty] private RolloutRecord? _interrupted;

    public bool HasInterrupted => Interrupted is not null;

    public bool IsRunning => Stage == RolloutStage.Running;
    public bool IsFailed => Stage == RolloutStage.Failed;
    public bool IsDone => Stage == RolloutStage.Done;

    /// <summary>What the machine that stopped is left holding. Named per exit, below each of them.</summary>
    public RolloutHostRowViewModel? Stopped => Rows.FirstOrDefault(r => r.IsFailed);

    /// <summary>How many machines are up, for the line above the rows.</summary>
    public string Progress =>
        Rows.Count == 0
            ? string.Empty
            : $"{Rows.Count(r => r.IsDone)} of {Rows.Count} machines ready";

    /// <summary>
    /// What closing the app now would do. Surfaced rather than left implicit: this run happens from
    /// here, so quitting stops it — and a rollout that dies silently on quit is the worst version.
    /// </summary>
    public string? ClosingWarning => IsRunning
        ? $"A rollout of {ClusterName} is running from this machine. Closing Kontena stops k0sctl "
          + "where it is, leaving a half-built cluster on the machines it already reached. Nothing is "
          + "undone. The next launch will offer to carry on from there."
        : null;

    /// <summary>
    /// The sentence that has to be on the failure screen. Its absence is what makes a missing rollback
    /// button read as an omission rather than a decision.
    /// </summary>
    public const string NoRollback =
        "Nothing has been undone. Rolling back a half-finished install would also take out the machines "
        + "that worked, which is further from a cluster than you are now.";

    // ── Running it ───────────────────────────────────────────────────────────

    /// <summary>
    /// Installs <paramref name="spec"/>. Re-runnable as-is: k0sctl is meant to be idempotent, so a
    /// machine that is already up is a no-op — which is what makes "retry this host" and "resume"
    /// the same command as the first attempt rather than a special path.
    /// </summary>
    public async Task RunAsync(
        RemoteClusterSpec spec, IClusterCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (IsRunning)
            return;

        ClusterName = spec.Name;
        Error = null;
        Output.Clear();

        _tracker = new RolloutTracker(spec.Hosts);

        // A resumed run starts with what the last one got done already on screen, rather than
        // pretending nothing happened and re-drawing it as it goes.
        if (Interrupted is { } record && string.Equals(record.ClusterName, spec.Name, StringComparison.Ordinal))
            _tracker.Restore(record.Standing);

        Rebuild();
        Stage = RolloutStage.Running;

        _running?.Dispose();
        _running = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await foreach (var line in _provisioner.CreateAsync(spec, credentials, _running.Token))
            {
                Output.Add(line.Text);
                _tracker.Consume(line.Text);
                Refresh();
            }

            _tracker.Finish();
            Stage = RolloutStage.Done;

            // It finished, so there is nothing left to resume.
            _records.Clear();
            Interrupted = null;
        }
        catch (OperationCanceledException)
        {
            Stop("Cancelled. k0sctl stopped where it was; nothing has been undone.");
        }
        catch (ToolFailedException exception)
        {
            Error = exception.Complaint;
            Stop(exception.Complaint);
        }
        catch (ToolNotFoundException exception)
        {
            Error = exception.Message;
            Stop(exception.Message);
        }
        finally
        {
            Rebuild();
            Save();

            _running?.Dispose();
            _running = null;
        }
    }

    /// <summary>Stops the run. Not a rollback — k0sctl leaves what it has already installed.</summary>
    [RelayCommand]
    private void Cancel() => _running?.Cancel();

    private void Stop(string? complaint)
    {
        _tracker?.Fail(complaint);
        Stage = RolloutStage.Failed;
    }

    // ── The three ways on from a failure ─────────────────────────────────────

    /// <summary>
    /// Try that machine again. The same <c>k0sctl apply</c> as before: the ones that are up stay up,
    /// and the one that stopped is picked up where it was. There is no per-host command to run — and
    /// inventing one would be a second code path that only runs on the worst day.
    /// </summary>
    public RolloutExit RetryHost => new(
        "Try this machine again",
        $"Runs the same install again. The {Rows.Count(r => r.IsDone)} machines already up are left "
        + "alone — k0sctl skips what is done — and this one is retried from where it stopped.",
        $"{Stopped?.Address} keeps whatever it has already installed. Nothing is removed first.");

    /// <summary>
    /// Build the cluster without it. Honest about the shape that leaves: a controller dropped from a
    /// three-controller plan is not the cluster that was designed.
    /// </summary>
    public RolloutExit ContinueWithout
    {
        get
        {
        var stopped = Stopped;
        var controllers = Rows.Count(r => r.IsController && !r.IsFailed);

        var consequence = stopped?.IsController == true
            ? $"The cluster is built with {controllers} controller{(controllers == 1 ? "" : "s")} instead of "
              + $"{controllers + 1}. {(controllers % 2 == 0 ? "That is an even number, so it survives no more failures than one fewer would." : "That is still a workable number.")}"
            : "The cluster is built without this worker. It can be added later.";

        return new RolloutExit(
            "Continue without this machine",
            consequence,
            $"{stopped?.Address} is left exactly as it is — part-installed, not cleaned up, and not part "
            + "of the cluster. Nothing on it is removed.");
        }
    }

    /// <summary>
    /// Look at what happened on that machine. Its own lines out of the console, not a rewording:
    /// k0sctl already said what went wrong, and the job is finding it in the wall.
    /// </summary>
    public RolloutExit Diagnose => new(
        "Diagnose this machine",
        "Shows every line the tool wrote about it, and the preflight answers it gave earlier.",
        $"{Stopped?.Address} is not touched by looking at it.");

    /// <summary>Every line of output that named the machine that stopped.</summary>
    public IReadOnlyList<string> StoppedLines => Stopped?.Lines ?? [];

    [ObservableProperty] private bool _isDiagnosing;

    [RelayCommand]
    private void ToggleDiagnosis() => IsDiagnosing = !IsDiagnosing;

    /// <summary>Leaves the machine out and carries on — the middle of the three exits.</summary>
    [RelayCommand]
    private void SkipStopped()
    {
        if (Stopped is { } row && _tracker is not null)
        {
            _tracker.Skip(row.Address);
            Rebuild();
            Save();
        }
    }

    /// <summary>Forgets an interrupted rollout without resuming it.</summary>
    [RelayCommand]
    private void DiscardInterrupted()
    {
        _records.Clear();
        Interrupted = null;
        OnPropertyChanged(nameof(HasInterrupted));
    }

    // ── Keeping track ────────────────────────────────────────────────────────

    private void Save()
    {
        if (_tracker is null || Stage == RolloutStage.Done)
            return;

        _records.Write(new RolloutRecord(
            ClusterName,
            [.. _tracker.Standing.Select(h => h.Address)],
            _tracker.Stopped?.Address,
            DateTimeOffset.UtcNow));
    }

    /// <summary>Called when the window is closing mid-rollout, so the next launch can carry on.</summary>
    public void Remember() => Save();

    private void Rebuild()
    {
        Rows.Clear();

        foreach (var host in _tracker?.Hosts ?? [])
            Rows.Add(new RolloutHostRowViewModel(host));

        Refresh();
    }

    private void Refresh()
    {
        foreach (var row in Rows)
            row.Refresh();

        foreach (var name in new[] { nameof(Progress), nameof(Stopped), nameof(StoppedLines), nameof(RetryHost),
                     nameof(ContinueWithout), nameof(Diagnose) })
            OnPropertyChanged(name);
    }

    partial void OnStageChanged(RolloutStage value)
    {
        foreach (var name in new[]
                 {
                     nameof(IsRunning), nameof(IsFailed), nameof(IsDone), nameof(ClosingWarning),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnInterruptedChanged(RolloutRecord? value) => OnPropertyChanged(nameof(HasInterrupted));
}

/// <summary>One way on from a failure: what it does, what it costs, and what the machine is left as.</summary>
/// <param name="Title">The button.</param>
/// <param name="Detail">What choosing it means for the cluster.</param>
/// <param name="LeftBehind">
/// The line underneath. Always says what state the machine is left in, because that is the question
/// someone actually has at that moment and no exit answers it by itself.
/// </param>
public sealed record RolloutExit(string Title, string Detail, string LeftBehind);

/// <summary>One machine's row while the rollout runs.</summary>
public sealed partial class RolloutHostRowViewModel(RolloutHost host) : ObservableObject
{
    public string Address => host.Address;

    public bool IsController => host.Role == ClusterHostRole.Controller;

    public string Role => IsController ? "controller" : "worker";

    public RolloutStep Step => host.Step;

    public RolloutHostState State => host.State;

    public string? Detail => host.Detail;

    public IReadOnlyList<string> Lines => host.Lines;

    public bool IsWaiting => State == RolloutHostState.Waiting;
    public bool IsBusy => State == RolloutHostState.Running;
    public bool IsDone => State == RolloutHostState.Done;
    public bool IsFailed => State == RolloutHostState.Failed;
    public bool IsSkipped => State == RolloutHostState.Skipped;

    /// <summary>The five steps, each marked done, current or still ahead.</summary>
    public IReadOnlyList<RolloutStepViewModel> Steps =>
    [
        .. Enum.GetValues<RolloutStep>().Select(step => new RolloutStepViewModel(
            step switch
            {
                RolloutStep.Connect => "connect",
                RolloutStep.Binary => "binary",
                RolloutStep.Join => "join",
                RolloutStep.Service => "service",
                _ => "healthy",
            },
            IsDone || step < Step,
            !IsDone && step == Step && IsBusy)),
    ];

    internal void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Step), nameof(State), nameof(Detail), nameof(Lines), nameof(Steps),
                     nameof(IsWaiting), nameof(IsBusy), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>One of the five steps on a machine's row.</summary>
/// <param name="Name">Its label.</param>
/// <param name="IsDone">Passed.</param>
/// <param name="IsCurrent">Happening now.</param>
public sealed record RolloutStepViewModel(string Name, bool IsDone, bool IsCurrent);
