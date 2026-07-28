using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;

namespace Kontena.App.ViewModels;

/// <summary>
/// Where you came from (KON-173).
/// <para>
/// There were five <c>Back</c> commands before this and none of them was navigation: each jumped to a
/// fixed destination, so every caller had to know where the user had come from. That is why opening a
/// pod from a workload needed an extra <c>onBack</c> parameter threaded through by hand — the history
/// replayed manually, one route at a time.
/// </para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// A place you can return to.
    /// <para>
    /// An action rather than a page instance, because cluster pages are rebuilt on every visit and a
    /// detail page disposes its streams when you leave it. Keeping the instance would hand back a
    /// dead object that renders as a blank page; keeping the <i>route</i> rebuilds it.
    /// </para>
    /// </summary>
    /// <param name="Label">For the tooltip — "Back to Pods".</param>
    /// <param name="Go">Re-performs this navigation.</param>
    /// <param name="Target">The object this step shows, when it shows one, so the step can be dropped
    /// if that object is deleted.</param>
    private sealed record NavStep(string Label, Action Go, object? Target = null);

    private readonly List<NavStep> _history = [];
    private NavStep? _here;

    /// <summary>Set while replaying, so a replayed navigation does not push itself back on.</summary>
    private bool _replaying;

    /// <summary>Deeper than this and the oldest entries fall off; nobody navigates back forty pages.</summary>
    private const int MaxHistory = 40;

    public bool CanGoBack => _history.Count > 0;

    public string BackTooltip => _history.Count > 0 ? $"Back to {_history[^1].Label}" : "Back";

    /// <summary>
    /// Record that we have arrived somewhere and how to arrive there again. Called by each navigation
    /// entry point rather than inferred, because only the caller knows what it just did.
    /// </summary>
    private void Arrived(string label, Action go, object? target = null)
    {
        if (_replaying)
            return;

        if (_here is { } previous)
        {
            _history.Add(previous);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        _here = new NavStep(label, go, target);
        NotifyHistoryChanged();
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_history.Count == 0)
            return;

        var step = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _here = step;

        _replaying = true;
        try
        {
            step.Go();
        }
        finally
        {
            _replaying = false;
        }

        NotifyHistoryChanged();
    }

    /// <summary>
    /// Drop every step that shows this object, because it no longer exists. Going back to the detail
    /// page of a pod you have just deleted is a page that can only fail to load — the step has to go
    /// at the moment of deletion, since nothing later can tell that it was ever valid.
    /// </summary>
    private void ForgetSteps(object target)
    {
        _history.RemoveAll(s => ReferenceEquals(s.Target, target));

        if (_here is { } here && ReferenceEquals(here.Target, target))
            _here = null;

        NotifyHistoryChanged();
    }

    /// <summary>
    /// Switching backend is not a previous page — it is a different world, with its own nav and its
    /// own pages. Carrying the stack across would offer a Back that lands somewhere that no longer
    /// has a menu entry.
    /// </summary>
    private void ClearHistory()
    {
        _history.Clear();
        _here = null;
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackTooltip));
        OnPropertyChanged(nameof(BackTooltipWithHint));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    // ── Keyboard (KON-172) ────────────────────────────────────────────────────

    /// <summary>
    /// Escape. Closes the modal if one is open; otherwise does nothing.
    /// <para>
    /// Deliberately not "otherwise go back". Escape means <i>dismiss what just appeared</i>, and
    /// making it navigate as well would mean a mistimed Escape leaves the page instead of the dialog
    /// you thought was still open.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (IsDialogOpen)
            CloseDialog();
    }

    /// <summary>Enter. Runs the open dialog's primary action, where it has one.</summary>
    [RelayCommand]
    private void ConfirmPrimary()
    {
        if (Dialog is IPrimaryAction { CanInvokePrimary: true } action)
            action.InvokePrimary();
    }

    /// <summary>
    /// Raised when the search box should take focus. The view owns focus, not the view model — this
    /// is the shell asking rather than reaching into the visual tree.
    /// </summary>
    public Action? RequestFocusSearch { get; set; }

    [RelayCommand]
    private void FocusSearch()
    {
        // Only where there is something to search; focusing a disabled box is a shortcut that appears
        // to do nothing (KON-164).
        if (IsSearchEnabled)
            RequestFocusSearch?.Invoke();
    }

    // ── Configurable shortcuts (KON-180) ──────────────────────────────────────

    /// <summary>
    /// What each action id in <see cref="ShellActions"/> actually runs. The window builds its key
    /// bindings from this and the stored gestures, rather than naming commands in XAML — which is what
    /// made them unchangeable.
    /// </summary>
    public IReadOnlyDictionary<string, ICommand> ShortcutCommands =>
        _shortcutCommands ??= new Dictionary<string, ICommand>(StringComparer.Ordinal)
        {
            [ShellActions.GoBack] = GoBackCommand,
            [ShellActions.FocusSearch] = FocusSearchCommand,
            [ShellActions.RefreshPage] = RefreshCurrentPageCommand,
            [ShellActions.Dismiss] = DismissCommand,
            [ShellActions.ConfirmPrimary] = ConfirmPrimaryCommand,
        };

    private IReadOnlyDictionary<string, ICommand>? _shortcutCommands;

    /// <summary>
    /// Raised when the shortcuts changed and the window should rebind. Keeps "no restart" honest: the
    /// bindings are a collection the view owns, so the shell asks rather than reaching into it.
    /// </summary>
    public Action? RequestRebindShortcuts { get; set; }

    /// <summary>The gesture in force for an action, for the tooltip of the button that does the same.</summary>
    public string ShortcutHint(string actionId)
    {
        var action = ShellActions.Find(actionId);
        return action is null
            ? string.Empty
            : ShellActions.Display(ShellActions.GestureFor(action, _settings.Shortcuts));
    }

    /// <summary>Back, with the keys that also do it — the shortcut is where the button is (KON-180).</summary>
    public string BackTooltipWithHint =>
        ShortcutHint(ShellActions.GoBack) is { Length: > 0 } keys ? $"{BackTooltip} ({keys})" : BackTooltip;

    public string RefreshTooltip =>
        ShortcutHint(ShellActions.RefreshPage) is { Length: > 0 } keys ? $"Refresh ({keys})" : "Refresh";

    /// <summary>
    /// The Settings page changed a shortcut. Re-read what is on disk — the settings page writes through
    /// its own copy — then rebind and redraw the tooltips that quote the keys.
    /// </summary>
    internal void ShortcutsChanged()
    {
        _settings = _store.Load();

        RequestRebindShortcuts?.Invoke();
        OnPropertyChanged(nameof(BackTooltipWithHint));
        OnPropertyChanged(nameof(RefreshTooltip));
    }
}
