using System.Globalization;
using System.Text.RegularExpressions;

namespace Kontena.App.ViewModels;

/// <summary>
/// Orders the cells of a server-rendered listing (KON-454).
/// <para>
/// The cells of a <see cref="Kontena.Sdk.Orchestration.Models.ResourceTable"/> are strings the API
/// server formatted — the same ones <c>kubectl get</c> prints — so there is no typed field behind them
/// to sort on. Ordinal order therefore puts <c>12h</c> before <c>3d</c>, <c>10</c> before <c>9</c> and
/// <c>512Mi</c> before <c>2Gi</c>, which is wrong in the three places a resource listing is most often
/// sorted. This reads the shapes Kubernetes actually prints and falls back to case-insensitive text
/// for everything else.
/// </para>
/// <para>
/// Deliberately per-cell rather than per-column: a custom resource's columns are declared by whoever
/// wrote the CRD, and <see cref="Kontena.Sdk.Orchestration.Models.ResourceColumn"/> carries a name and
/// a priority but no type. Guessing a column's type from its header would be guessing at someone
/// else's vocabulary; the value itself is the only thing that says what it is.
/// </para>
/// </summary>
public static partial class ResourceCellOrder
{
    /// <summary>A ratio such as <c>2/3</c> — kubectl's READY column.</summary>
    [GeneratedRegex(@"^(\d+)\s*/\s*(\d+)$")]
    private static partial Regex Ratio { get; }

    /// <summary>
    /// A duration as kubectl prints an age: one or more number+unit pairs, e.g. <c>5d</c>, <c>3d4h</c>,
    /// <c>90m</c>. CPU millicores (<c>120m</c>) land here too, which is harmless: the same rule is
    /// applied to every cell in the column, so the order it produces is still the numeric one.
    /// </summary>
    [GeneratedRegex(@"^(?:\d+(?:y|d|h|m|s))+$")]
    private static partial Regex Duration { get; }

    /// <summary>Every number+unit pair inside a duration.</summary>
    [GeneratedRegex(@"(\d+)(y|d|h|m|s)")]
    private static partial Regex DurationPart { get; }

    /// <summary>A quantity with an optional binary or decimal suffix, and a plain number.</summary>
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(Ki|Mi|Gi|Ti|Pi|Ei|k|M|G|T|P|E)?$")]
    private static partial Regex Quantity { get; }

    /// <summary>
    /// A sort key for one cell: whether it reads as a number, the number if it does, and the text it
    /// came from. A tuple because the ordering it needs is exactly a tuple's — numbers among
    /// themselves first, then everything else by text — and because it is total, so a column of
    /// numbers holding a single <c>&lt;none&gt;</c> orders instead of throwing.
    /// </summary>
    public static (int Rank, double Number, string Text) Of(string? cell)
    {
        var text = cell?.Trim() ?? string.Empty;

        if (text.Length == 0)
            return Text(string.Empty);

        if (Ratio.Match(text) is { Success: true } ratio)
        {
            var ready = double.Parse(ratio.Groups[1].Value, CultureInfo.InvariantCulture);
            var total = double.Parse(ratio.Groups[2].Value, CultureInfo.InvariantCulture);

            // By how complete it is, so 0/2 sorts before 1/1 — a listing is sorted on READY to find
            // what is not ready, not to group by replica count.
            return Number(total == 0 ? 0 : ready / total, text);
        }

        if (Duration.IsMatch(text))
        {
            var seconds = 0d;
            foreach (Match part in DurationPart.Matches(text))
            {
                var value = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
                seconds += value * part.Groups[2].Value switch
                {
                    "y" => 31_536_000d,
                    "d" => 86_400d,
                    "h" => 3_600d,
                    "m" => 60d,
                    _ => 1d,
                };
            }

            return Number(seconds, text);
        }

        if (Quantity.Match(text) is { Success: true } quantity)
        {
            var value = double.Parse(quantity.Groups[1].Value, CultureInfo.InvariantCulture);
            return Number(value * Scale(quantity.Groups[2].Value), text);
        }

        return Text(text);
    }

    private static (int, double, string) Number(double value, string text) => (0, value, text);

    /// <summary>Lower-cased so the ordinal comparison a tuple does reads as case-insensitive.</summary>
    private static (int, double, string) Text(string text) => (1, 0, text.ToLowerInvariant());

    private static double Scale(string suffix) => suffix switch
    {
        "Ki" => 1024d,
        "Mi" => 1024d * 1024,
        "Gi" => 1024d * 1024 * 1024,
        "Ti" => 1024d * 1024 * 1024 * 1024,
        "Pi" => 1024d * 1024 * 1024 * 1024 * 1024,
        "Ei" => 1024d * 1024 * 1024 * 1024 * 1024 * 1024,
        "k" => 1_000d,
        "M" => 1_000_000d,
        "G" => 1_000_000_000d,
        "T" => 1_000_000_000_000d,
        "P" => 1_000_000_000_000_000d,
        "E" => 1_000_000_000_000_000_000d,
        _ => 1d,
    };
}
