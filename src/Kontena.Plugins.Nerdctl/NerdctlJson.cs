using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kontena.Plugins.Nerdctl;

/// <summary>
/// nerdctl has no socket, so this plugin's entire view of the world is whatever the CLI printed —
/// captured against a real containerd (see Notes/nerdctl-cli-formats.md, KON-141) rather than assumed
/// from its docs, because a format taken from documentation nobody ran is a format nobody has checked.
/// Four things here surprised that capture: NDJSON instead of an array, human-readable sizes instead of
/// bytes, two different date layouts depending on the command, and labels as one comma-joined string.
/// </summary>
public static partial class NerdctlJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Splits <c>--format json</c> output into its individual objects. nerdctl prints one JSON object
    /// per line rather than a JSON array — <see cref="Parse{T}"/> depends on this, and "no volumes"
    /// prints nothing at all (not "[]", not a blank line), which splitting on both line-ending styles
    /// and dropping empty entries handles for free.
    /// </summary>
    public static IEnumerable<string> Lines(string stdout) =>
        stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Deserializes NDJSON — one <typeparamref name="T"/> per line — into a list.</summary>
    public static IReadOnlyList<T> Parse<T>(string stdout) =>
        [.. Lines(stdout).Select(line => JsonSerializer.Deserialize<T>(line, Options)!)];

    [GeneratedRegex(@"^\s*(?<number>[0-9.]+)\s*(?<unit>[A-Za-z]+)\s*$")]
    private static partial Regex SizePattern();

    private static readonly Dictionary<string, double> SizeUnits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 1,
            ["KB"] = 1_000,
            ["MB"] = 1_000_000,
            ["GB"] = 1_000_000_000,
            ["TB"] = 1_000_000_000_000,
        };

    /// <summary>
    /// Reads a size the way nerdctl prints one — a human string with a decimal unit, e.g. "53.98MB" —
    /// not the byte count Docker's API gives. nerdctl's units are powers of 1000, not 1024: "53.98MB" is
    /// 53.98 &#215; 10^6. Anything it cannot read comes back as 0 rather than throwing, because one odd
    /// value in a size column is not worth losing the whole list over.
    /// </summary>
    public static long Size(string text)
    {
        var match = SizePattern().Match(text);
        if (!match.Success)
            return 0;

        if (!double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return 0;

        if (!SizeUnits.TryGetValue(match.Groups["unit"].Value, out var multiplier))
            return 0;

        return (long)Math.Round(number * multiplier);
    }

    // Go's default time layout has no colon in its offset ("+0000"), which .NET's "zzz" specifier
    // cannot parse directly. Inserting the colon lets `TryParseExact` read it as if it were "+00:00".
    [GeneratedRegex(@"([+-])(\d{2})(\d{2})\b")]
    private static partial Regex OffsetWithoutColon();

    private static readonly string[] GoTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss zzz 'UTC'",
        "yyyy-MM-dd HH:mm:ss.ffffff zzz 'UTC'",
    ];

    /// <summary>
    /// Reads a timestamp regardless of which of nerdctl's two observed layouts it is in: <c>ps</c>
    /// gives ISO8601, <c>images</c> gives Go's default <c>time.Time</c> string
    /// ("2026-07-30 22:10:58 +0000 UTC"). One parser is not enough for both. Anything neither layout
    /// matches comes back as <c>default</c> rather than throwing — the same fail-soft contract as
    /// <see cref="Size"/>, for the same reason.
    /// </summary>
    public static DateTimeOffset Time(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
            return iso;

        var withColon = OffsetWithoutColon().Replace(text, "$1$2:$3");
        if (DateTimeOffset.TryParseExact(withColon, GoTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var go))
            return go;

        return default;
    }

    /// <summary>
    /// Splits the one comma-joined string nerdctl gives for labels — <c>ps</c> and <c>images</c> both
    /// print <c>Labels</c> this way, not as a JSON object — into a map. Split on the first '=' only, so
    /// a value that itself contains '=' survives intact.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(string text)
    {
        var labels = new Dictionary<string, string>();

        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
                continue;

            labels[pair[..separator]] = pair[(separator + 1)..];
        }

        return labels;
    }
}
