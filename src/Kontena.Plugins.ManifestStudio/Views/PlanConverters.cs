using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Plugins.ManifestStudio.Views;

/// <summary>One line of a unified diff, already sorted into the three kinds the plan view colours
/// (KON-427). Everything that is neither added, removed nor a hunk header is context.</summary>
public sealed record DiffLine(string Text)
{
    public bool IsAdd => Text.StartsWith('+') && !Text.StartsWith("+++", StringComparison.Ordinal);
    public bool IsDelete => Text.StartsWith('-') && !Text.StartsWith("---", StringComparison.Ordinal);
    public bool IsHeader => Text.StartsWith("@@", StringComparison.Ordinal)
        || Text.StartsWith("+++", StringComparison.Ordinal)
        || Text.StartsWith("---", StringComparison.Ordinal);
}

/// <summary>Splits a unified diff into lines the view can colour. A converter rather than a property on
/// the view model, so <c>ApplyProgress</c> — an SDK type shared with the shell — stays what the engine
/// streams and does not grow a presentation shape for this one page.</summary>
public sealed class DiffLinesConverter : IValueConverter
{
    public static readonly DiffLinesConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } diff
            ? diff.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(l => new DiffLine(l)).ToList()
            : new List<DiffLine>();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The wash and the ink for one diff line.
/// <para>
/// Literal colours, and deliberately so: a diff sits on <c>Console</c>, which stays dark in both themes
/// (KON-196 — a terminal is a terminal). These are the only two roles in the plugin where a theme-aware
/// pair would be answering a question nobody asked.
/// </para>
/// </summary>
public sealed class DiffColourConverter(bool ink) : IValueConverter
{
    public static readonly DiffColourConverter Wash = new(ink: false);
    public static readonly DiffColourConverter Ink = new(ink: true);

    private static readonly IBrush AddWash = new SolidColorBrush(Color.Parse("#1A34D399"));
    private static readonly IBrush DeleteWash = new SolidColorBrush(Color.Parse("#1AF87171"));
    private static readonly IBrush AddInk = new SolidColorBrush(Color.Parse("#7EE7C7"));
    private static readonly IBrush DeleteInk = new SolidColorBrush(Color.Parse("#FFB3B3"));
    private static readonly IBrush HeaderInk = new SolidColorBrush(Color.Parse("#7CC4FF"));
    private static readonly IBrush ContextInk = new SolidColorBrush(Color.Parse("#8B949E"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DiffLine line)
            return null;

        if (!ink)
            return line switch
            {
                { IsHeader: true } => null,
                { IsAdd: true } => AddWash,
                { IsDelete: true } => DeleteWash,
                _ => null,
            };

        return line switch
        {
            { IsHeader: true } => HeaderInk,
            { IsAdd: true } => AddInk,
            { IsDelete: true } => DeleteInk,
            _ => ContextInk,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Answers the plan row's questions about an <see cref="ApplyAction"/>: what to call it, and which of
/// the four groups it belongs to so the badge can carry the right colour.
/// <para>
/// The words keep the dry-run's tense. "Create" is what a plan says it would do; "Created" is what an
/// apply reports it did — collapsing the two would let a preview read as a change that happened, which
/// is the one thing the notice above the list exists to deny.
/// </para>
/// </summary>
public sealed class ApplyActionConverter : IValueConverter
{
    public static readonly ApplyActionConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ApplyAction action)
            return false;

        return parameter as string switch
        {
            "label" => Label(action),
            "created" => action is ApplyAction.Created or ApplyAction.WouldCreate,
            "changed" => action is ApplyAction.Configured or ApplyAction.WouldChange,
            "failed" => action is ApplyAction.Failed,
            "deferred" => action is ApplyAction.Deferred,
            _ => false,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Label(ApplyAction action) => action switch
    {
        ApplyAction.Created => "Created",
        ApplyAction.WouldCreate => "Create",
        ApplyAction.Configured => "Updated",
        ApplyAction.WouldChange => "Update",
        ApplyAction.Unchanged => "Unchanged",
        ApplyAction.Deferred => "Deferred",
        ApplyAction.Failed => "Failed",
        _ => action.ToString(),
    };
}
