using Avalonia.Input;
using Kontena.Core.Orchestration;

namespace Kontena.App.Services;

/// <summary>
/// One thing the shell can be asked to do from the keyboard, and the keys that ask for it (KON-180).
/// </summary>
/// <param name="Id">Stable across releases — it is what settings key off. Never rename one.</param>
/// <param name="Label">What Settings shows.</param>
/// <param name="Description">What it does, for the row under the label.</param>
/// <param name="Gesture">The default everywhere but macOS.</param>
/// <param name="MacGesture">
/// The macOS default, where the convention differs. Null means the same keys on every platform —
/// <c>Escape</c> and <c>Enter</c> are not a Ctrl/Cmd question.
/// </param>
public sealed record ShellAction(
    string Id, string Label, string Description, string Gesture, string? MacGesture = null)
{
    /// <summary>
    /// The default on the platform this is running on.
    /// <para>
    /// KON-172 registered both variants at once, so <c>Ctrl+F</c> also worked on macOS, where it is not
    /// the convention. One platform, one default: a macOS user who wants Ctrl can still set it.
    /// </para>
    /// </summary>
    public string DefaultGesture =>
        OperatingSystem.IsMacOS() && MacGesture is { Length: > 0 } mac ? mac : Gesture;
}

/// <summary>Whether a gesture may be bound, and why not when it may not.</summary>
/// <param name="Problem">Null when the gesture is fine.</param>
public sealed record GestureCheck(string? Problem)
{
    public bool Ok => Problem is null;

    public static readonly GestureCheck Fine = new((string?)null);
}

/// <summary>
/// Every keyboard action the shell offers, with its default keys (KON-180).
/// <para>
/// Before this the mapping existed only as a binding name in XAML, which meant it could not be shown,
/// changed or checked. This registry is the single list: the window builds its bindings from it,
/// Settings renders it, and the ⌘K palette (KON-148) is meant to take it as one of its sources rather
/// than keeping a second list of the same actions in step.
/// </para>
/// </summary>
public static class ShellActions
{
    public const string Dismiss = "dialog.dismiss";
    public const string ConfirmPrimary = "dialog.confirm";
    public const string GoBack = "nav.back";
    public const string FocusSearch = "search.focus";
    public const string RefreshPage = "page.refresh";
    public const string ToggleFullScreen = "window.fullscreen";

    public static IReadOnlyList<ShellAction> All { get; } =
    [
        new(GoBack, "Back", "Return to the page you came from.", "Alt+Left", "Cmd+Left"),
        new(FocusSearch, "Focus search", "Put the cursor in the search box of the current list.", "Ctrl+F", "Cmd+F"),
        new(RefreshPage, "Refresh page", "Reload what the current page shows.", "Ctrl+R", "Cmd+R"),

        // The window's own, not a page's — and the only entry here the shell rather than the view model
        // answers (KON-361). macOS routes ⌃⌘F through the green title-bar button, which
        // WindowDecorations="BorderOnly" removes, so the system shortcut reaches nothing and the app has
        // to own it. F11 is the same gesture everywhere else, and this is the one place the two
        // platforms mean exactly the same thing.
        new(ToggleFullScreen, "Full screen", "Fill the screen with Kontena, and leave it again.", "F11", "Ctrl+Cmd+F"),
        new(Dismiss, "Close dialog", "Close the open dialog. Does nothing when none is open.", "Escape"),
        new(ConfirmPrimary, "Confirm dialog", "Run the open dialog's primary action, where it has one.", "Enter"),
    ];

    /// <summary>
    /// Keys the terminal must keep, because they control the <i>process</i> rather than the line.
    /// <para>
    /// The line-editing keys a shell also answers — <c>Ctrl+A</c>, <c>Ctrl+E</c>, <c>Ctrl+R</c> and the
    /// rest — are deliberately <b>not</b> here, because a shortcut only shadows a terminal while its
    /// command can execute and these all belong to pages rather than to the terminal's own page. What
    /// this paragraph used to claim — that a focused terminal handles a window binding first — is
    /// simply false: a <c>TopLevel</c> matches its <c>KeyBindings</c> before the focused control sees
    /// the key, so any command that can always execute eats its key everywhere. That is what stopped
    /// Enter and Escape from reaching a shell at all (KON-201), and why the dialog commands are gated
    /// on a dialog being open.
    /// Interrupting, ending and suspending are different: they are the way out of a stuck process, and
    /// a shortcut that quietly shadows them everywhere except the terminal is not worth the confusion.
    /// </para>
    /// </summary>
    private static readonly string[] ReservedGestures = ["Ctrl+C", "Ctrl+D", "Ctrl+Z", "Ctrl+OemBackslash"];

    public static ShellAction? Find(string id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The gesture in force for an action: what the user chose, or the default where they chose nothing.
    /// <para>
    /// Absent means default rather than "no shortcut", which is what lets a better default in a later
    /// release actually reach the people who never touched it.
    /// </para>
    /// </summary>
    public static string GestureFor(ShellAction action, IReadOnlyDictionary<string, string>? configured)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (configured is not null
            && configured.TryGetValue(action.Id, out var chosen)
            && Normalise(chosen) is { Length: > 0 } valid)
        {
            return valid;
        }

        // Normalised, not as authored: Avalonia's name for the Enter key is Return, and a row whose
        // stored spelling differs from its own default would show as changed while it is not.
        return Normalise(action.DefaultGesture);
    }

    /// <summary>Every action with the gesture currently in force, in the order Settings shows them.</summary>
    public static IReadOnlyList<(ShellAction Action, string Gesture)> Resolve(
        IReadOnlyDictionary<string, string>? configured) =>
        [.. All.Select(a => (a, GestureFor(a, configured)))];

    /// <summary>
    /// The canonical spelling of a gesture, or empty where it is not one. Comparison and storage both
    /// go through this, so <c>ctrl+f</c> and <c>Ctrl + F</c> cannot be stored as two different keys.
    /// </summary>
    public static string Normalise(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return string.Empty;

        try
        {
            return KeyGesture.Parse(gesture).ToString();
        }
        catch (ArgumentException)
        {
            // Not a gesture. Callers treat empty as "unusable" rather than crashing on typed input.
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Whether <paramref name="gesture"/> can be given to <paramref name="actionId"/>, and what is wrong
    /// when it cannot. A conflict is reported rather than resolved: silently letting the last one win
    /// leaves a shortcut that used to work and now does nothing, with nothing on screen saying why.
    /// </summary>
    public static GestureCheck Check(
        string actionId, string gesture, IReadOnlyDictionary<string, string>? configured)
    {
        var normalised = Normalise(gesture);
        if (normalised.Length == 0)
            return new GestureCheck("That is not a key combination Kontena can use.");

        if (ReservedGestures.Any(r => string.Equals(Normalise(r), normalised, StringComparison.Ordinal)))
        {
            return new GestureCheck(
                $"{Display(normalised)} belongs to the terminal — it interrupts, ends or suspends what is running there.");
        }

        var clash = Resolve(configured)
            .FirstOrDefault(p => !string.Equals(p.Action.Id, actionId, StringComparison.Ordinal)
                                 && string.Equals(p.Gesture, normalised, StringComparison.Ordinal));

        return clash.Action is { } taken
            ? new GestureCheck($"{Display(normalised)} is already {taken.Label}.")
            : GestureCheck.Fine;
    }

    /// <summary>
    /// How a gesture is written on screen. Avalonia names keys after their virtual key — <c>Return</c>
    /// for the one every keyboard has printed as Enter, <c>OemBackslash</c> for the backslash — and a
    /// label nobody can find on their keyboard is not a label.
    /// </summary>
    public static string Display(string gesture) =>
        (gesture ?? string.Empty)
        .Replace("OemBackslash", "\\", StringComparison.Ordinal)
        .Replace("Return", "Enter", StringComparison.Ordinal);
}
