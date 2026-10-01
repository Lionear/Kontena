using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// The detail page of any object Kontena has no page of its own for — every custom resource, and the
/// built-in kinds that only live on the Resources page (KON-483).
/// <para>
/// A row there used to open its YAML and nothing else, which made a CloudNativePG cluster a second-class
/// thing next to a Deployment: no conditions, no events, no way to its pods. This is the same tabbed page
/// the typed kinds have, built from what any object can say about itself without a model of its kind —
/// metadata, the printer columns its CRD declares, the scalar fields of its status and the conditions
/// that by convention sit beside them.
/// </para>
/// </summary>
public sealed partial class ClusterCustomResourceDetailViewModel : ClusterObjectDetailViewModel
{
    private readonly Action<ResourceRef>? _onOpen;
    private ResourceObject? _object;

    /// <param name="onOpen">Opens another object — an owner, or a workload that uses this one. The
    /// shell's, which knows which page each kind has.</param>
    public ClusterCustomResourceDetailViewModel(
        IClusterEngine cluster, ResourceRef reference,
        Action<Pod>? onOpenPod = null, Action<ResourceRef>? onOpen = null, Action? onDelete = null,
        string initialTab = "overview")
        : base(cluster, reference, onOpenPod, onDelete)
    {
        _onOpen = onOpen;
        SelectedTab = initialTab;

        _ = LoadAsync();
    }

    public string Kind => Reference.Kind.Kind;

    /// <summary>Group and version — what tells CloudNativePG's Cluster from Cluster API's.</summary>
    public string ApiVersion => Reference.Kind.IsCoreGroup
        ? Reference.Kind.Version
        : $"{Reference.Kind.Group}/{Reference.Kind.Version}";

    public bool HasNamespace => Namespace.Length > 0;

    [ObservableProperty] private bool _isLoading = true;

    /// <summary>Why the overview has nothing to show, when it has not.</summary>
    [ObservableProperty] private string? _loadNote;

    public string AgeText => _object?.Created is { } created ? Format.Age(created) : "—";

    /// <summary>The printer columns, then the status fields they did not already cover.</summary>
    public IReadOnlyList<ResourceField> Fields { get; private set; } = [];

    public bool HasFields => Fields.Count > 0;

    public IReadOnlyList<ResourceConditionRow> Conditions { get; private set; } = [];

    public bool HasConditions => Conditions.Count > 0;

    public IReadOnlyList<ResourceField> Labels { get; private set; } = [];

    public bool HasLabels => Labels.Count > 0;

    public IReadOnlyList<ResourceField> Annotations { get; private set; } = [];

    public bool HasAnnotations => Annotations.Count > 0;

    public IReadOnlyList<ResourceLinkRow> Owners { get; private set; } = [];

    public bool HasOwners => Owners.Count > 0;

    /// <summary>The workloads that use this object, or that it created (KON-455).</summary>
    [ObservableProperty] private IReadOnlyList<ResourceUsageRow> _users = [];

    /// <summary>
    /// True once the question has been asked and answered, however it came out. Without it the empty
    /// state cannot tell "nothing uses this" from "not looked yet".
    /// </summary>
    [ObservableProperty] private bool _usersChecked;

    public bool HasUsers => Users.Count > 0;

    /// <summary>
    /// Nothing found, and what was looked at — a generic ladder will sometimes miss a custom resource
    /// that links itself in a way none of the three rungs covers, and silence there would read as
    /// "nothing uses this".
    /// </summary>
    public bool NothingUsesIt => UsersChecked && Users.Count == 0;

    private async Task LoadAsync()
    {
        await ReadObjectAsync();
        await LoadUsersAsync();

        // After the users: the pods of a workload this object created are its pods too.
        await LoadPodsAsync();
    }

    private bool _refreshing;
    private bool _refreshAgain;

    /// <summary>
    /// Re-read when the watch says it moved — the whole point of the page for a status (KON-483).
    /// <para>
    /// Coalesced, unlike the typed pages: an operator rewrites its object's status many times a second
    /// while it works (CloudNativePG scaling a cluster sent dozens of Modified events per second against
    /// kind), and the base loop awaits each one, so one read per event fell further behind the cluster
    /// the longer the operator worked. Returning at once and reading again only once the read in flight
    /// is done keeps the page on the latest state with at most one read out.
    /// </para>
    /// </summary>
    protected override Task OnResourceModifiedAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return Task.CompletedTask;
        }

        _ = RefreshAsync();
        return Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await ReadObjectAsync();
                await RefreshPodsAsync();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ReadObjectAsync()
    {
        try
        {
            if (await Cluster.GetObjectAsync(Reference) is { } fresh)
            {
                Show(fresh);
                LoadNote = null;
            }
            else if (_object is null)
            {
                LoadNote = "This cluster has no such object to show. It may have been deleted.";
            }
        }
        catch (Exception ex)
        {
            // A failed re-read keeps what is on screen: a page a few seconds behind is a smaller lie than
            // one that blanks fields it can no longer vouch for (KON-450).
            if (_object is null)
                LoadNote = "Could not read this object: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show(ResourceObject o)
    {
        _object = o;

        // Age is in the header already, and a status field a column of the same name shows would be
        // the same fact twice. By name only: two fields that happen to hold the same value are not the
        // same field.
        var shown = o.Columns.Where(c => !c.Name.Equals("Age", StringComparison.OrdinalIgnoreCase)).ToArray();
        Fields =
        [
            .. shown,
            .. o.Status.Where(s => !shown.Any(c => c.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))),
        ];

        Conditions = [.. o.Conditions.Select(c => new ResourceConditionRow(c))];
        Labels = [.. o.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new ResourceField(l.Key, l.Value))];

        // kubectl's last-applied copy is the whole manifest again, in one line; it belongs to the YAML tab.
        Annotations =
        [
            .. o.Annotations
                .Where(a => a.Key != "kubectl.kubernetes.io/last-applied-configuration")
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new ResourceField(a.Key, a.Value)),
        ];
        Owners = [.. o.Owners.Select(r => new ResourceLinkRow(r, _onOpen))];

        foreach (var name in new[]
                 {
                     nameof(AgeText), nameof(Fields), nameof(HasFields), nameof(Conditions), nameof(HasConditions),
                     nameof(Labels), nameof(HasLabels), nameof(Annotations), nameof(HasAnnotations),
                     nameof(Owners), nameof(HasOwners),
                 })
            OnPropertyChanged(name);
    }

    private async Task LoadUsersAsync()
    {
        IReadOnlyList<ResourceUsage> usages;

        try
        {
            usages = await Cluster.FindUsersAsync(Reference);
        }
        catch (Exception)
        {
            // An engine that cannot answer leaves the section empty rather than the page broken.
            usages = [];
        }

        Users = [.. usages.Select(u => new ResourceUsageRow(u, _onOpen))];
        UsersChecked = true;
        OnPropertyChanged(nameof(HasUsers));
        OnPropertyChanged(nameof(NothingUsesIt));
    }

    /// <summary>
    /// Pods this object controls, and pods of the workloads it created — an operator's own StatefulSet
    /// is how most custom resources run anything, and its pods are the ones you came looking for.
    /// </summary>
    protected override IReadOnlyList<Pod> SelectPods(IReadOnlyList<Pod> all)
    {
        // The same owner string the pods carry, where a ReplicaSet is already rolled up to its
        // Deployment — so "Deployment/x" covers a created Deployment across its revisions.
        var owners = Users
            .Where(u => u.IsCreatedByThis)
            .Select(u => $"{u.Reference.Kind.Kind}/{u.Name}")
            .Append($"{Kind}/{Name}")
            .ToHashSet(StringComparer.Ordinal);

        return [.. all.Where(p => owners.Contains(p.ControlledBy))];
    }

    protected override string EmptyPodsReason() =>
        "No pod is controlled by this object, or by a workload it created.";
}

/// <summary>
/// One entry of <c>status.conditions</c>, coloured by whether it reads as healthy (KON-483).
/// </summary>
public sealed class ResourceConditionRow(ResourceCondition condition)
{
    public string Type => condition.Type;

    public string State => condition.Status;

    public string Detail => condition.Message.Length > 0 ? condition.Message : condition.Reason;

    public string Since => condition.LastTransition is { } at ? Format.Age(at) : string.Empty;

    /// <summary>
    /// Green for a condition in its good state, amber for one that is not, grey for Unknown.
    /// <para>
    /// Most conditions are positive — Ready, Available — but some are named for the trouble they
    /// report, and True is then the bad answer.
    /// </para>
    /// </summary>
    // ponytail: the bad-when-true names are a word list, not knowledge of the kind; a CRD that names a
    // negative condition some other way shows green for it. The State column always says the raw value.
    public IBrush Brush => new SolidColorBrush(Color.Parse(condition.Status switch
    {
        "True" => IsNegative(condition.Type) ? "#F5B14C" : "#34D399",
        "False" => IsNegative(condition.Type) ? "#34D399" : "#F5B14C",
        _ => "#5C6675",
    }));

    private static readonly string[] Trouble = ["Degraded", "Fail", "Error", "Stalled", "Pressure", "Unavailable"];

    private static bool IsNegative(string type) =>
        Trouble.Any(t => type.Contains(t, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A clickable reference to another object — an owner of the object on the page.</summary>
public sealed partial class ResourceLinkRow(ResourceRef reference, Action<ResourceRef>? onOpen)
{
    public ResourceRef Reference { get; } = reference;

    public string Name => Reference.Name;

    public string KindText => Reference.Kind.Kind;

    [RelayCommand]
    private void Open() => onOpen?.Invoke(Reference);
}

/// <summary>
/// One workload that uses, or was created by, the object being shown (KON-455). Clicking it opens
/// that workload — the same click-through a related pod row has had since the workload detail
/// existed, pointed the other way.
/// </summary>
public sealed partial class ResourceUsageRow(ResourceUsage usage, Action<ResourceRef>? onOpen)
{
    public ResourceRef Reference { get; } = usage.User;

    public string Name => Reference.Name;

    /// <summary>Kind and namespace, the way every other row in the app spells a location.</summary>
    public string Where => Reference.Namespace is { Length: > 0 } ns
        ? $"{Reference.Kind.Kind} · namespace {ns}"
        : Reference.Kind.Kind;

    public bool IsCreatedByThis => usage.OwnedByTarget;

    /// <summary>
    /// Which way the relation runs. "Created by this" and "uses this" end up in the same list and are
    /// not the same fact — an operator's own StatefulSet is not a consumer of the object.
    /// </summary>
    public string Direction => usage.OwnedByTarget ? "created by this" : "uses this";

    /// <summary>
    /// How the link was found, in the cluster's own words. On the row because a relation whose basis
    /// is not on screen cannot be checked — and these bases are not equally strong.
    /// </summary>
    public string Evidence => usage.Evidence switch
    {
        UsageEvidence.OwnerReference => "ownerReference",
        UsageEvidence.Mount => $"mounts {usage.Detail}",
        _ => $"annotation {usage.Detail}",
    };

    [RelayCommand]
    private void Open() => onOpen?.Invoke(Reference);
}
