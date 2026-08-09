using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// Every event in the selected namespace, newest first (KON-248).
/// <para>
/// <c>ListEventsAsync</c> was only ever called from pod detail and object detail, which means the
/// events of an object could only be found once the object had been. That is the wrong way round for
/// the question this data answers: you come to the events feed precisely because you do not yet know
/// which object is the broken one.
/// </para>
/// </summary>
public partial class ClusterEventsViewModel : ClusterListPageViewModel<ClusterEventRow>
{
    private readonly IClusterEngine _cluster;
    private readonly string? _namespace;
    private readonly Func<ResourceRef, Task<bool>>? _onOpenObject;

    /// <param name="onOpenObject">Opens the object an event is about; returns false when it no longer
    /// exists. Events outlive the things they describe, so that is an ordinary outcome and not an
    /// error.</param>
    /// <remarks>
    /// Following the cluster matters more here than on any other list (KON-340). This is the feed you
    /// open <em>because</em> something is wrong, and until now it froze at the moment you opened it —
    /// so the event explaining what happened next was already outside the page you were staring at.
    /// </remarks>
    public ClusterEventsViewModel(
        IClusterEngine cluster, string? @namespace, Func<ResourceRef, Task<bool>>? onOpenObject = null)
        : base(cluster, GroupVersionKind.Event, @namespace)
    {
        _cluster = cluster;
        _namespace = @namespace;
        _onOpenObject = onOpenObject;
        _ = LoadAsync();
        StartWatching();
    }

    public override string SearchPlaceholder => "Search events…";

    /// <summary>
    /// Show only warnings. Off by default: a feed that hides the Normal events cannot show that a
    /// rollout completed right after the warning that preceded it, and that sequence is often the
    /// whole answer.
    /// </summary>
    [ObservableProperty] private bool _warningsOnly;

    partial void OnWarningsOnlyChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void ToggleWarningsOnly() => WarningsOnly = !WarningsOnly;

    protected override bool Include(ClusterEventRow row) => !WarningsOnly || row.IsWarning;

    /// <summary>How many of the loaded events are warnings — the number on the toggle.</summary>
    public int WarningCount => All.Count(r => r.IsWarning);

    protected override async Task<IReadOnlyList<ClusterEventRow>> LoadRowsAsync()
    {
        var events = await _cluster.ListEventsAsync(_namespace);

        // Newest first. The API returns them in no order worth relying on, and "what just happened"
        // is the entire reason for opening this page.
        return
        [
            .. events
                .OrderByDescending(e => e.LastSeen)
                .Select(e => new ClusterEventRow(e, OpenObjectAsync)),
        ];
    }

    // The reason and the message both: "BackOff" is how you filter to a class of trouble, and the
    // message is where the registry's own words are. The object name is how you follow one thing.
    protected override bool Matches(ClusterEventRow row, string term) =>
        Contains(row.Reason, term) || Contains(row.Message, term)
        || Contains(row.ObjectName, term) || Contains(row.Source, term);

    protected override IReadOnlyDictionary<string, Func<ClusterEventRow, IComparable>> SortColumns { get; } =
        new Dictionary<string, Func<ClusterEventRow, IComparable>>(StringComparer.Ordinal)
        {
            ["TYPE"] = r => r.Severity,
            ["REASON"] = r => r.Reason,
            ["OBJECT"] = r => r.ObjectName,
            ["COUNT"] = r => r.CountRaw,
            ["LAST SEEN"] = r => r.LastSeenAt,
        };

    protected override void OnFiltered()
    {
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    /// <summary>
    /// Set when following an event's object failed. Events survive the objects they are about, so a
    /// pod that has been replaced is the common case — saying so beats a link that does nothing.
    /// </summary>
    [ObservableProperty] private string? _navigationNotice;

    partial void OnNavigationNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    /// <summary>
    /// The one line under the table: why it is empty, or why a link went nowhere. An empty feed is a
    /// real answer here and a good one — a cluster where nothing is happening.
    /// </summary>
    public string? Notice => NavigationNotice ?? (
        !HasLoaded || Items.Count > 0 ? null
        : All.Count == 0 ? "No events in this namespace. Kubernetes keeps these for about an hour, so"
                           + " a quiet feed means nothing has happened recently — not that nothing ever did."
        : WarningsOnly ? "No warnings in this namespace. Turn the filter off to see everything that happened."
        : "No events match that search.");

    public bool HasNotice => Notice is not null;

    private async Task OpenObjectAsync(ResourceRef target)
    {
        NavigationNotice = null;

        if (_onOpenObject is null)
            return;

        if (!await _onOpenObject(target))
        {
            NavigationNotice =
                $"{target.Kind.Kind} \"{target.Name}\" is not there any more. Events outlive the objects"
                + " they are about, so this one describes something that has since been replaced or removed.";
        }
    }
}

/// <summary>One row of the cluster events feed.</summary>
public sealed partial class ClusterEventRow
{
    private readonly ResourceRef _target;
    private readonly Func<ResourceRef, Task>? _open;

    public ClusterEventRow(ClusterEvent e, Func<ResourceRef, Task>? open = null)
    {
        ArgumentNullException.ThrowIfNull(e);

        _target = e.InvolvedObject;
        _open = open;

        Reason = e.Reason;
        Message = e.Message;
        Source = string.IsNullOrEmpty(e.Source) ? "—" : e.Source;
        IsWarning = e.Severity == EventSeverity.Warning;
        Severity = IsWarning ? "Warning" : "Normal";

        var kind = e.InvolvedObject.Kind.Kind;
        ObjectName = string.IsNullOrEmpty(e.InvolvedObject.Name)
            ? "—"
            : string.IsNullOrEmpty(kind) ? e.InvolvedObject.Name : $"{kind}/{e.InvolvedObject.Name}";

        ObjectTooltip = string.IsNullOrEmpty(e.InvolvedObject.Namespace)
            ? null
            : $"in {e.InvolvedObject.Namespace}";

        // Only the kinds that have a detail page to arrive at. A link that opens nothing is worse
        // than plain text, and every other kind is reachable through the resource browser anyway.
        CanOpen = open is not null && Navigable.Contains(kind);

        // An event that fired 340 times is a different fact from one that fired once, and it is the
        // count that tells a crash loop from a single bad start.
        CountText = e.Count > 1 ? $"×{e.Count}" : string.Empty;
        IsRepeating = e.Count > 1;
        CountRaw = e.Count;

        LastSeenAt = e.LastSeen;
        LastSeen = Format.Age(e.LastSeen);

        SeverityBrush = new SolidColorBrush(Color.Parse(IsWarning ? "#F5B14C" : "#5C6675"));
    }

    /// <summary>Kinds this app can open a detail page for.</summary>
    private static readonly HashSet<string> Navigable =
        new(StringComparer.Ordinal)
        {
            "Pod", "Deployment", "StatefulSet", "DaemonSet", "Job", "CronJob", "Service",
        };

    public string Severity { get; }
    public string Reason { get; }
    public string Message { get; }
    public string Source { get; }
    public string ObjectName { get; }
    public string? ObjectTooltip { get; }
    public string CountText { get; }
    public bool IsRepeating { get; }

    /// <summary>What a column sort orders COUNT by — <see cref="CountText"/> is blank below 2.</summary>
    public int CountRaw { get; }

    public string LastSeen { get; }

    /// <summary>The timestamp behind <see cref="LastSeen"/> — what the feed is ordered by.</summary>
    public DateTimeOffset LastSeenAt { get; }
    public bool IsWarning { get; }
    public bool CanOpen { get; }
    public IBrush SeverityBrush { get; }

    [RelayCommand]
    private async Task Open()
    {
        if (_open is not null)
            await _open(_target);
    }
}
