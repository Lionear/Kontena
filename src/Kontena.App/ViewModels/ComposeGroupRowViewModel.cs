using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>
/// One Compose project as a heading in the Containers list (KON-159).
/// </summary>
/// <remarks>
/// <para>
/// It carries a name, how much of the stack is up, and the project actions. Deliberately no image, no
/// ports and no CPU or memory: a sum of four containers' CPU is either a lie or meaningless, and an
/// image column holding four different images has nothing to put in it. What it does carry is the one
/// thing the flat list could not say — whether the stack as a whole is healthy.
/// </para>
/// <para>
/// This does not replace the Projects page. Grouping here is for <i>seeing</i> — where does my stack
/// run, what is red. The Projects page is for operating a project as a whole: up from a file,
/// aggregated logs. Hence the link on this row rather than a second home for the same thing.
/// </para>
/// </remarks>
public sealed partial class ComposeGroupRowViewModel : ContainerListRowViewModel
{
    private readonly ContainersViewModel _parent;
    private IReadOnlyList<ContainerRowViewModel> _children;

    public ComposeGroupRowViewModel(
        string name, IReadOnlyList<ContainerRowViewModel> children, ContainersViewModel parent)
    {
        Name = name;
        _children = children;
        _parent = parent;
    }

    public string Name { get; }

    public override string SortKey => Name;

    /// <summary>Patch the row after a reload, keeping the instance so its expansion survives.</summary>
    public void Update(IReadOnlyList<ContainerRowViewModel> children)
    {
        _children = children;

        foreach (var property in new[]
                 {
                     nameof(TotalCount), nameof(RunningCount), nameof(SummaryText),
                     nameof(StatusBrush), nameof(CanStart), nameof(CanStop),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    public IReadOnlyList<ContainerRowViewModel> Children => _children;

    public IReadOnlyList<string> ContainerIds => [.. _children.Select(c => c.Id)];

    public int TotalCount => _children.Count;

    public int RunningCount => _children.Count(c => c.IsRunning);

    /// <summary>
    /// "3 of 4 running". Always the fraction, even when it is all of them: a bare "running" makes the
    /// reader work out whether anything is missing, which is the question this row exists to answer.
    /// </summary>
    public string SummaryText =>
        string.Create(CultureInfo.InvariantCulture, $"{RunningCount} of {TotalCount} running");

    /// <summary>
    /// The worst state in the group, because that is the one worth walking over to. Paired with the
    /// text above, never colour alone (KON-56).
    /// </summary>
    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(
        _children.Any(c => c.Summary.State is ContainerState.Exited or ContainerState.Dead) ? "#F87171"
        : _children.Any(c => c.Summary.State is ContainerState.Paused or ContainerState.Restarting) ? "#F5B14C"
        : _children.Count > 0 && _children.All(c => c.IsRunning) ? "#34D399"
        : "#808B9B"));

    public bool CanStart => _children.Any(c => !c.IsRunning);
    public bool CanStop => _children.Any(c => c.IsRunning);

    /// <summary>
    /// What the user set. Survives a reload because the row instance does — and a reload happens after
    /// every action, so a group that collapsed on each start would make the whole mode unusable.
    /// <para>
    /// Shut to begin with: the point of grouping is that a stack takes one line instead of four, and
    /// starting every project open is the flat list again with extra rows in it. Opening one is a
    /// question someone asks about that project.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>
    /// Open because a search matched inside it, which is not the same as the user opening it. Kept
    /// apart so that clearing the search puts every group back exactly as they left it.
    /// </summary>
    [ObservableProperty] private bool _isForcedOpen;

    /// <summary>
    /// Set once the user shut this group <i>during</i> a search. Without it the next redraw would open
    /// it again — the search is still matching — and the click would look like it did nothing.
    /// Cleared when the query changes, because that is a new question.
    /// </summary>
    public bool ClosedDuringSearch { get; private set; }

    /// <summary>A new query, so any "I closed this one" from the previous search stops applying.</summary>
    public void ForgetSearchOverride() => ClosedDuringSearch = false;

    /// <summary>Whether children are on screen right now — what the chevron points at.</summary>
    public bool IsOpen => IsExpanded || IsForcedOpen;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsOpen));
    partial void OnIsForcedOpenChanged(bool value) => OnPropertyChanged(nameof(IsOpen));

    [RelayCommand]
    private void Toggle()
    {
        // While a search holds it open, the first click should shut it rather than appear to do
        // nothing — so the forced flag yields to a deliberate one, and stays yielded.
        if (IsForcedOpen)
        {
            IsForcedOpen = false;
            IsExpanded = false;
            ClosedDuringSearch = true;
        }
        else
        {
            IsExpanded = !IsExpanded;
        }

        _parent.RefreshRows();
    }

    [RelayCommand]
    private Task Start() => _parent.StartProjectAsync(ContainerIds);

    [RelayCommand]
    private Task Stop() => _parent.StopProjectAsync(ContainerIds);

    [RelayCommand]
    private Task Restart() => _parent.RestartProjectAsync(ContainerIds);

    [RelayCommand]
    private void Down() => _parent.ConfirmDown(this);

    [RelayCommand]
    private void OpenProject() => _parent.OpenProject(Name);
}
