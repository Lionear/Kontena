using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Orchestration;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.App.ViewModels;

/// <summary>
/// The machines a remote cluster will be installed on — step 2 of the provisioning flow (KON-233).
/// <para>
/// Nothing here touches a machine. That happens at the preflight, one step later, and the table says
/// so by having nothing to say about any host's state: no status column, no dot, no "checking". A
/// column that is empty until something checks it reads as a check that already ran and found nothing.
/// </para>
/// </summary>
/// <remarks>
/// Its own view model rather than a list on the wizard, for the same reason the create form is its
/// own: it has rows with their own life cycle, validation of the set rather than of a field, and an
/// import that replaces the lot.
/// <para>
/// Every rule it shows comes from <see cref="RemoteClusterSpec"/> (KON-232) rather than from here. A
/// second wording of the quorum argument is a second thing to keep true.
/// </para>
/// </remarks>
public sealed partial class HostInventoryViewModel : ObservableObject
{
    /// <summary>
    /// What an empty table asks for. Names the requirement rather than the absence: "no hosts" is
    /// something the user can already see, and does not say that one of them has to be a controller.
    /// </summary>
    public const string Empty =
        "No machines yet. Add at least one controller — that is the machine that runs the control " +
        "plane, and a cluster cannot be built without one. Workers can come later.";

    /// <summary>Bindable form of <see cref="Empty"/>, so the view has one wording to show, not two.</summary>
    public string EmptyMessage => Empty;

    public ObservableCollection<HostRowViewModel> Hosts { get; } = [];

    public bool IsEmpty => Hosts.Count == 0;

    /// <summary>Rows that are actually about a machine. A half-typed row is not counted yet.</summary>
    private IReadOnlyList<RemoteClusterHost> Built =>
        [.. Hosts.Select(h => h.Host).OfType<RemoteClusterHost>()];

    public int ControllerCount => Built.Count(h => h.Role == ClusterHostRole.Controller);

    public int WorkerCount => Built.Count(h => h.Role == ClusterHostRole.Worker);

    /// <summary>"3 controllers · 2 workers", the running total next to the buttons.</summary>
    public string Summary =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ControllerCount} {Plural(ControllerCount, "controller")} · {WorkerCount} {Plural(WorkerCount, "worker")}");

    /// <summary>
    /// What blocks this list, or null. Straight from the spec's own rule, minus the empty case — an
    /// empty table already says what it needs, and saying it twice in two wordings is worse than once.
    /// </summary>
    public string? Problem => IsEmpty ? null : RemoteClusterSpec.HostsProblem(Built);

    public bool HasProblem => Problem is not null;

    /// <summary>The quorum argument for this many controllers, or null when the count is sensible.</summary>
    public string? Warning => RemoteClusterSpec.QuorumWarning(ControllerCount);

    public bool HasWarning => Warning is not null;

    /// <summary>What the last import did, or null when none has run.</summary>
    [ObservableProperty] private string? _importMessage;

    /// <summary>The machines as the spec wants them.</summary>
    public IReadOnlyList<RemoteClusterHost> Build() => Built;

    [RelayCommand]
    public void AddHost()
    {
        Add(new HostRowViewModel(Remove)
        {
            // First machine is a controller: it is the one a cluster cannot do without, and it is what
            // an empty table just asked for. Later rows default to worker, which is the common case.
            Role = Hosts.Count == 0 ? ClusterHostRole.Controller : ClusterHostRole.Worker,
        });

        Recompute();
    }

    /// <summary>
    /// Adds the machines from a <c>k0sctl.yaml</c>, skipping addresses already in the table. Adding
    /// rather than replacing: an import next to hand-typed rows should not throw them away, and a
    /// duplicate address is the one mistake the table would otherwise be left holding.
    /// </summary>
    public void ImportK0sctl(string? yaml)
    {
        var found = K0sctlImport.ReadHosts(yaml);

        if (found.Count == 0)
        {
            ImportMessage = "No machines found in that file — expected a k0sctl.yaml with a spec.hosts list.";
            return;
        }

        var known = Built.Select(h => h.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var host in found.Where(h => known.Add(h.Address)))
        {
            Add(new HostRowViewModel(Remove)
            {
                Address = host.Address,
                Role = host.Role,
                NodeName = host.NodeName ?? string.Empty,
                User = host.User ?? string.Empty,
                KeyPath = host.KeyPath ?? string.Empty,
            });

            added++;
        }

        var skipped = found.Count - added;

        ImportMessage = skipped == 0
            ? $"Imported {added} {Plural(added, "machine")}."
            : $"Imported {added} {Plural(added, "machine")}; skipped {skipped} already in the table.";

        Recompute();
    }

    private void Add(HostRowViewModel row)
    {
        row.Edited += (_, _) => Recompute();
        Hosts.Add(row);
    }

    private void Remove(HostRowViewModel row)
    {
        Hosts.Remove(row);
        Recompute();
    }

    private void Recompute()
    {
        foreach (var name in new[]
                 {
                     nameof(IsEmpty), nameof(ControllerCount), nameof(WorkerCount), nameof(Summary),
                     nameof(Problem), nameof(HasProblem), nameof(Warning), nameof(HasWarning),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private static string Plural(int count, string word) => count == 1 ? word : word + "s";
}
