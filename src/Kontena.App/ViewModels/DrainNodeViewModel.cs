using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The "Drain node" modal (KON-251): what a drain is about to do, then what it did, one pod at a
/// time.
/// <para>
/// A progress list rather than a spinner because a drain is not one outcome. Some pods move, some
/// are left where they are for reasons worth reading, and a PodDisruptionBudget may refuse — and
/// that last one is a true statement about the cluster's own rules, not a failure.
/// </para>
/// </summary>
public partial class DrainNodeViewModel : ViewModelBase, IDisposable
{
    private readonly IClusterEngine _cluster;
    private readonly Action _onClose;
    private readonly Func<Task> _onDone;
    private CancellationTokenSource? _running;

    public DrainNodeViewModel(IClusterEngine cluster, string node, Action onClose, Func<Task> onDone)
    {
        _cluster = cluster;
        _onClose = onClose;
        _onDone = onDone;
        Node = node;
    }

    public string Node { get; }

    /// <summary>
    /// Whether to evict pods holding an <c>emptyDir</c>, whose contents go with them. Its own
    /// question, and off until asked: this is the only thing on this dialog that destroys anything,
    /// and folding it into the Drain button would understate what that button does.
    /// </summary>
    [ObservableProperty] private bool _deleteEmptyDirData;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasFinished;
    [ObservableProperty] private string? _error;

    public ObservableCollection<DrainStepRow> Steps { get; } = [];

    public bool HasSteps => Steps.Count > 0;

    public bool CanDrain => !IsRunning && !HasFinished;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanDrain));
    partial void OnHasFinishedChanged(bool value) => OnPropertyChanged(nameof(CanDrain));

    /// <summary>
    /// What a drain does not promise, said before it starts rather than after it disappoints.
    /// </summary>
    public string Caveat { get; } =
        "The node is marked unschedulable first, so nothing new lands on it while this runs."
        + " Pods managed by a DaemonSet stay — their controller would put them straight back — and a"
        + " PodDisruptionBudget may refuse to let a pod go, which is the budget doing its job."
        + " Nothing is rolled back if this stops part way: the node stays cordoned, which is the safe"
        + " place for it to be.";

    /// <summary>The one line after it is over: what moved, what stayed, what was refused.</summary>
    [ObservableProperty] private string? _summary;

    [RelayCommand]
    private async Task DrainAsync()
    {
        if (!CanDrain)
            return;

        IsRunning = true;
        Error = null;
        Steps.Clear();
        OnPropertyChanged(nameof(HasSteps));

        _running = new CancellationTokenSource();

        var evicted = 0;
        var skipped = 0;
        var blocked = 0;
        var failed = 0;

        try
        {
            var options = new DrainOptions { DeleteEmptyDirData = DeleteEmptyDirData };
            Services.Diag.Action("drain node", Node);

            await foreach (var step in _cluster.DrainNodeAsync(Node, options, _running.Token))
            {
                switch (step.Action)
                {
                    case DrainAction.Evicted: evicted++; break;
                    case DrainAction.Skipped: skipped++; break;
                    case DrainAction.Blocked: blocked++; break;
                    case DrainAction.Failed: failed++; break;
                    default: break;
                }

                // Evicting then Evicted is the same pod twice; the row is updated rather than added,
                // so the list reads as a set of pods and not as a log of state changes.
                if (step.Action == DrainAction.Evicted
                    && Steps.FirstOrDefault(s => s.Pod == step.Pod && s.Namespace == step.Namespace) is { } existing)
                {
                    existing.Update(step);
                }
                else
                {
                    Steps.Add(new DrainStepRow(step));
                }

                OnPropertyChanged(nameof(HasSteps));
            }

            Summary = Describe(evicted, skipped, blocked, failed);
            HasFinished = true;
            await _onDone();
        }
        catch (OperationCanceledException)
        {
            // Stopping is a choice, not an error — but it leaves the node cordoned, and that has to
            // be said rather than assumed.
            Summary = $"Stopped after {evicted} moved. {Node} is still cordoned; uncordon it when you are ready.";
            HasFinished = true;
        }
        catch (Exception failure)
        {
            Error = failure.Message;
        }
        finally
        {
            IsRunning = false;
            _running?.Dispose();
            _running = null;
        }
    }

    /// <summary>
    /// The outcome in one line, counting each kind separately. "Drained" alone would be true of a
    /// run where three pods were refused and one node is no emptier than it was.
    /// </summary>
    private string Describe(int evicted, int skipped, int blocked, int failed)
    {
        var parts = new List<string> { $"{evicted} moved" };

        if (skipped > 0)
            parts.Add($"{skipped} left in place");
        if (blocked > 0)
            parts.Add($"{blocked} refused by a disruption budget");
        if (failed > 0)
            parts.Add($"{failed} did not go");

        var tail = blocked > 0 || failed > 0
            ? $" {Node} is not empty. It stays cordoned until you uncordon it."
            : $" {Node} stays cordoned until you uncordon it.";

        return string.Join(", ", parts) + "." + tail;
    }

    [RelayCommand]
    private void Stop() => _running?.Cancel();

    [RelayCommand]
    private void Close()
    {
        Dispose();
        _onClose();
    }

    /// <summary>
    /// Closing the dialog stops the drain but undoes nothing: the pods already moved stay moved and
    /// the node stays cordoned, which is the state this whole flow is careful to leave behind.
    /// </summary>
    public void Dispose()
    {
        _running?.Cancel();

        // Disposing a source whose token the drain loop is still waiting on turns a clean cancel
        // into an ObjectDisposedException, so the running loop's own finally keeps that job. Both
        // run on the UI thread, so which of the two owns it is settled rather than raced.
        if (!IsRunning)
        {
            _running?.Dispose();
            _running = null;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>One pod's line in the drain progress list.</summary>
public sealed partial class DrainStepRow : ObservableObject
{
    public DrainStepRow(DrainProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);

        Pod = step.Pod;
        Namespace = step.Namespace;
        Update(step);
    }

    public string Pod { get; }
    public string Namespace { get; }

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#5C6675"));

    /// <summary>Whether this line is about a pod at all — the cordon and finish steps are not.</summary>
    public bool IsPod => Pod.Length > 0;

    public void Update(DrainProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);

        Status = step.Action switch
        {
            DrainAction.Cordoned => "Cordoned",
            DrainAction.Skipped => "Left in place",
            DrainAction.Evicting => "Moving…",
            DrainAction.Evicted => "Moved",
            DrainAction.Blocked => "Refused",
            DrainAction.Failed => "Did not go",
            _ => "Done",
        };

        Reason = string.IsNullOrEmpty(step.Reason) ? null : step.Reason;

        StatusBrush = new SolidColorBrush(Color.Parse(step.Action switch
        {
            DrainAction.Evicted or DrainAction.Cordoned or DrainAction.Finished => "#34D399",
            DrainAction.Evicting => "#F5B14C",
            DrainAction.Blocked => "#F5B14C",
            DrainAction.Failed => "#F87171",
            _ => "#5C6675",
        }));
    }
}
