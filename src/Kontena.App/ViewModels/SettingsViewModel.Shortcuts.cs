using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kontena.App.Services;
using Kontena.Core.Orchestration;

namespace Kontena.App.ViewModels;

/// <summary>
/// One shortcut in Settings › General › Keyboard (KON-180).
/// <para>
/// The gesture is shown, not typed. <c>KeyGesture.Parse</c> throws on anything that is not a gesture,
/// and asking someone to spell <c>Alt+Left</c> correctly is asking them to know a syntax; pressing the
/// keys is the thing they already know.
/// </para>
/// </summary>
public partial class ShortcutRow : ViewModelBase
{
    private readonly Func<string, string, bool> _apply;
    private readonly Func<string, bool> _reset;

    public ShortcutRow(ShellAction action, string gesture, Func<string, string, bool> apply,
        Func<string, bool> reset)
    {
        ArgumentNullException.ThrowIfNull(action);

        Action = action;
        _apply = apply;
        _reset = reset;
        _gesture = gesture;
    }

    public ShellAction Action { get; }

    public string Label => Action.Label;
    public string Description => Action.Description;

    [ObservableProperty] private string _gesture;

    partial void OnGestureChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayGesture));
        OnPropertyChanged(nameof(IsDefault));
    }

    /// <summary>What the row shows — a key name nobody reads on a keyboard is not a label.</summary>
    public string DisplayGesture => ShellActions.Display(Gesture);

    /// <summary>Whether this is still the shipped default; the reset button only means something when it is not.</summary>
    public bool IsDefault =>
        string.Equals(Gesture, ShellActions.Normalise(Action.DefaultGesture), StringComparison.Ordinal);

    /// <summary>
    /// Whether this is the bottom row. The separator belongs between rows, not under the last one —
    /// the card already draws its own edge there, and two hairlines a pixel apart read as a mistake.
    /// </summary>
    [ObservableProperty] private bool _isLast;

    /// <summary>Set while this row is waiting for a key combination. Only one row can be, at a time.</summary>
    [ObservableProperty] private bool _isRecording;

    /// <summary>Why the last attempt was refused, or empty. Cleared as soon as one is accepted.</summary>
    [ObservableProperty] private string _problem = string.Empty;

    public bool HasProblem => Problem.Length > 0;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    [RelayCommand]
    private void Record()
    {
        Problem = string.Empty;
        IsRecording = true;
    }

    [RelayCommand]
    private void CancelRecord() => IsRecording = false;

    [RelayCommand]
    private void Reset() => _reset(Action.Id);

    /// <summary>
    /// The view has captured a combination. Returns whether it was taken, so the recorder knows whether
    /// to stay open — a refused gesture leaves the row listening rather than making you click again.
    /// </summary>
    public bool Offer(string gesture)
    {
        var accepted = _apply(Action.Id, gesture);
        if (accepted)
            IsRecording = false;

        return accepted;
    }
}

/// <summary>
/// Settings › General › Keyboard (KON-180) — which keys do what, and putting them back.
/// </summary>
public partial class SettingsViewModel
{
    private Dictionary<string, string> _shortcutOverrides = [];

    /// <summary>
    /// The shell, so a changed shortcut takes effect without a restart. An init property rather than a
    /// fourteenth constructor parameter, following <see cref="LocalClusters"/>.
    /// </summary>
    public Action? RequestShortcutsChanged { get; init; }

    public ObservableCollection<ShortcutRow> Shortcuts { get; } = [];

    /// <summary>True once anything deviates from the defaults — what "Restore defaults" is for.</summary>
    public bool HasCustomShortcuts => _shortcutOverrides.Count > 0;

    private void RefreshShortcuts()
    {
        _shortcutOverrides = new Dictionary<string, string>(_settings.Shortcuts, StringComparer.Ordinal);

        Shortcuts.Clear();
        foreach (var (action, gesture) in ShellActions.Resolve(_shortcutOverrides))
            Shortcuts.Add(new ShortcutRow(action, gesture, ApplyShortcut, ResetShortcut));

        if (Shortcuts.Count > 0)
            Shortcuts[^1].IsLast = true;

        OnPropertyChanged(nameof(HasCustomShortcuts));
    }

    /// <summary>
    /// Give an action a gesture, unless something is wrong with it. Returns whether it was taken.
    /// </summary>
    private bool ApplyShortcut(string actionId, string gesture)
    {
        var row = Shortcuts.FirstOrDefault(r => string.Equals(r.Action.Id, actionId, StringComparison.Ordinal));
        if (row is null)
            return false;

        var check = ShellActions.Check(actionId, gesture, _shortcutOverrides);
        if (!check.Ok)
        {
            row.Problem = check.Problem!;
            return false;
        }

        var normalised = ShellActions.Normalise(gesture);

        // Back to the default is stored as nothing, not as the default's current spelling — otherwise
        // "I set it back" would pin today's value and a later release could not improve it.
        if (string.Equals(normalised, ShellActions.Normalise(row.Action.DefaultGesture), StringComparison.Ordinal))
            _shortcutOverrides.Remove(actionId);
        else
            _shortcutOverrides[actionId] = normalised;

        row.Problem = string.Empty;
        row.Gesture = normalised;
        SaveShortcuts();
        return true;
    }

    /// <summary>
    /// Put one action back to its default, unless another action is holding that gesture — which can
    /// happen when this one was moved out of the way first. Refusing says which one; resolving it
    /// silently would leave two bindings on the same keys and both would fire.
    /// </summary>
    private bool ResetShortcut(string actionId)
    {
        var row = Shortcuts.FirstOrDefault(r => string.Equals(r.Action.Id, actionId, StringComparison.Ordinal));
        if (row is null)
            return false;

        var without = new Dictionary<string, string>(_shortcutOverrides, StringComparer.Ordinal);
        without.Remove(actionId);

        var check = ShellActions.Check(actionId, row.Action.DefaultGesture, without);
        if (!check.Ok)
        {
            row.Problem = check.Problem!;
            return false;
        }

        _shortcutOverrides = without;
        row.Problem = string.Empty;
        row.Gesture = ShellActions.Normalise(row.Action.DefaultGesture);
        SaveShortcuts();
        return true;
    }

    /// <summary>
    /// Everything back to defaults. Always possible, whatever state the individual rows are in — which
    /// is what makes a per-row refusal safe to live with.
    /// </summary>
    [RelayCommand]
    private void ResetAllShortcuts()
    {
        _shortcutOverrides.Clear();

        foreach (var row in Shortcuts)
        {
            row.Problem = string.Empty;
            row.IsRecording = false;
            row.Gesture = ShellActions.Normalise(row.Action.DefaultGesture);
        }

        SaveShortcuts();
    }

    private void SaveShortcuts()
    {
        OnPropertyChanged(nameof(HasCustomShortcuts));
        Save();
        RequestShortcutsChanged?.Invoke();
    }
}
