using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kontena.Sdk.Models;

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
    /// <exception cref="JsonException">
    /// A line is not valid JSON, or is the literal <c>null</c> — every caller here goes on to read a
    /// property off each entry, so a null one must fail here rather than as a
    /// <see cref="NullReferenceException"/> wherever it is next dereferenced. This is the one place
    /// that guard needs writing: every command's output passes through here.
    /// </exception>
    public static IReadOnlyList<T> Parse<T>(string stdout) where T : class =>
        [.. Lines(stdout).Select(line => RequireObject(JsonSerializer.Deserialize<T>(line, Options)))];

    /// <summary>
    /// Deserializes a genuine JSON array — <c>nerdctl inspect</c> is the one command here that prints
    /// this shape instead of NDJSON (a single-element array, Docker-compatible; see
    /// Notes/nerdctl-cli-formats.md).
    /// </summary>
    /// <exception cref="JsonException">The array itself is malformed, or one of its elements is the
    /// literal <c>null</c> — same reasoning as <see cref="Parse{T}"/>.</exception>
    public static IReadOnlyList<T> ParseArray<T>(string stdout) where T : class =>
        JsonSerializer.Deserialize<List<T?>>(stdout, Options)?.Select(RequireObject).ToList() ?? [];

    /// <summary>A deserialized element that turned out to be JSON <c>null</c> is exactly as unusable to
    /// every caller as malformed JSON — both mean nerdctl printed something this plugin cannot read —
    /// so both are reported the same way rather than one becoming a deferred <see cref="NullReferenceException"/>.</summary>
    private static T RequireObject<T>(T? value) where T : class =>
        value ?? throw new JsonException($"Expected a {typeof(T).Name} object but found JSON null.");

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

    private static readonly Dictionary<string, double> BinarySizeUnits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 1,
            ["KiB"] = 1024,
            ["MiB"] = 1024L * 1024,
            ["GiB"] = 1024L * 1024 * 1024,
            ["TiB"] = 1024L * 1024 * 1024 * 1024,
        };

    /// <summary>
    /// Reads a size the way <c>images</c> prints one — a human string with a <b>decimal</b> unit, e.g.
    /// "53.98MB" — not the byte count Docker's API gives. nerdctl's units are powers of 1000 there, not
    /// 1024: "53.98MB" is 53.98 &#215; 10^6. Anything it cannot read comes back as 0 rather than
    /// throwing, because one odd value in a size column is not worth losing the whole list over.
    /// <para>
    /// <b>This is not the parser for <c>stats</c>.</b> That command prints binary units ("13.11MiB",
    /// "62.7GiB") for the same kind of value — see <see cref="BinarySize"/>. Reading a memory figure
    /// with this method is ~5% off and nothing in the output says so, which is why the two live side by
    /// side instead of one being made to cover both.
    /// </para>
    /// </summary>
    public static long Size(string text) => Size(text, SizeUnits);

    /// <summary>
    /// Reads a size the way <c>stats</c> prints one — a human string with a <b>binary</b> unit, e.g.
    /// "13.11MiB" or "62.7GiB" (Notes/nerdctl-advanced-formats.md). Same fail-soft contract as
    /// <see cref="Size"/>, which is the decimal counterpart for <c>images</c>; the two commands really do
    /// disagree on units, so both parsers exist on purpose.
    /// </summary>
    public static long BinarySize(string text) => Size(text, BinarySizeUnits);

    private static long Size(string text, Dictionary<string, double> units)
    {
        var match = SizePattern().Match(text);
        if (!match.Success)
            return 0;

        if (!double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return 0;

        if (!units.TryGetValue(match.Groups["unit"].Value, out var multiplier))
            return 0;

        return (long)Math.Round(number * multiplier);
    }

    /// <summary>
    /// Splits the three <c>stats</c> fields that carry two values in one string —
    /// <c>MemUsage</c> is <c>"13.11MiB / 62.7GiB"</c> (used / limit), <c>NetIO</c> and <c>BlockIO</c> are
    /// <c>"&lt;in&gt; / &lt;out&gt;"</c>. A field without the separator yields the whole text as the
    /// first half and an empty second half, which the size parsers then read as 0 — the same fail-soft
    /// contract as <see cref="Size"/> rather than a throw over one odd row.
    /// </summary>
    public static (string First, string Second) Pair(string text)
    {
        var separator = text.IndexOf('/', StringComparison.Ordinal);
        return separator < 0
            ? (text.Trim(), string.Empty)
            : (text[..separator].Trim(), text[(separator + 1)..].Trim());
    }

    /// <summary>
    /// Reads a <c>stats</c> percentage — <c>"0.00%"</c>, <c>"12.5%"</c> — as a number. The <c>%</c> is
    /// part of the value nerdctl prints, so it is stripped here rather than by every caller. Unreadable
    /// input comes back as 0, same reasoning as <see cref="Size"/>.
    /// </summary>
    public static double Percent(string text)
    {
        var trimmed = text.Trim().TrimEnd('%');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    // Go's default time layout has no colon in its offset ("+0000"), which .NET's "zzz" specifier
    // cannot parse directly. Inserting the colon lets `TryParseExact` read it as if it were "+00:00".
    [GeneratedRegex(@"([+-])(\d{2})(\d{2})\b")]
    private static partial Regex OffsetWithoutColon();

    // `images` prints this column with Go's `.Local()` (nerdctl's pkg/cmd/image/list.go), so the
    // trailing zone name is whatever the host's timezone happens to be ("CEST", "JST", "EDT", ...), not
    // always "UTC" — the fixture only says UTC because it was captured inside a container with no
    // timezone set. .NET has no way to parse an arbitrary zone abbreviation (they are not even unique:
    // "CST" alone names three different offsets), so the format below does not try: it parses only the
    // date, time and numeric offset — the first three space-separated tokens — and lets whatever zone
    // name follows go unread instead of assuming it is "UTC".
    //
    // Go's `time.Time.String()` calls `Round(time.Second)` before formatting, so this column never
    // carries fractional seconds — a ".ffffff" variant would match nothing this CLI ever prints, so
    // there is only the one layout here.
    private const string GoTimeFormat = "yyyy-MM-dd HH:mm:ss zzz";

    /// <summary>
    /// Reads a timestamp regardless of which of nerdctl's two observed layouts it is in: <c>ps</c>
    /// gives ISO8601, <c>images</c> gives Go's default <c>time.Time</c> string
    /// ("2026-07-30 22:10:58 +0000 UTC", zone name varying with the host — see <see cref="GoTimeFormat"/>).
    /// One parser is not enough for both. Anything neither layout matches comes back as <c>default</c>
    /// rather than throwing — the same fail-soft contract as <see cref="Size"/>, for the same reason.
    /// </summary>
    public static DateTimeOffset Time(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
            return iso;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            var dateTimeAndOffset = string.Join(' ', parts[0], parts[1], parts[2]);
            var withColon = OffsetWithoutColon().Replace(dateTimeAndOffset, "$1$2:$3");
            if (DateTimeOffset.TryParseExact(withColon, GoTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var go))
                return go;
        }

        return default;
    }

    [GeneratedRegex(@"^(?<host>.*):(?<hostport>\d+)->(?<containerport>\d+)/(?<protocol>[A-Za-z]+)$")]
    private static partial Regex PortPattern();

    /// <summary>
    /// Reads <c>ps</c>'s <c>Ports</c> column — a comma-separated human string, e.g.
    /// <c>"0.0.0.0:8080->80/tcp, 0.0.0.0:9090->90/udp"</c> — not the structured list Docker's API gives
    /// for the same field (see <c>DockerEngine.MapPorts</c>). The common case is an empty string (most
    /// captured containers publish nothing), which yields an empty list. A fragment that does not match
    /// the pattern is skipped rather than throwing — the same fail-soft contract as <see cref="Size"/>
    /// and <see cref="Time"/>: one odd binding is not worth losing the whole container list over.
    /// </summary>
    public static IReadOnlyList<PortBinding> Ports(string text)
    {
        var bindings = new List<PortBinding>();

        foreach (var fragment in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PortPattern().Match(fragment.Trim());
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["hostport"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort))
                continue;
            if (!int.TryParse(match.Groups["containerport"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort))
                continue;

            bindings.Add(new PortBinding(hostPort, containerPort, match.Groups["protocol"].Value));
        }

        return bindings;
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

    [GeneratedRegex(@"^level=(?<level>[a-z]+)\s+msg=""(?<message>.*)""\s*$")]
    private static partial Regex LogrusPattern();

    /// <summary>
    /// Unwraps one of nerdctl's logrus lines — <c>compose</c> narrates entirely this way, on stderr:
    /// <c>level=info msg="Creating container cmp-web-1"</c> (Notes/nerdctl-advanced-formats.md). The
    /// sentence a user needs is inside <c>msg</c>; showing the whole line would put nerdctl's own log
    /// plumbing on screen. A line that is not logrus-shaped (compose also prints bare lines) comes back
    /// unchanged with a <c>null</c> level, so a caller can pass it through as-is.
    /// </summary>
    public static (string? Level, string Message) Logrus(string line)
    {
        var match = LogrusPattern().Match(line.Trim());
        return match.Success
            ? (match.Groups["level"].Value, match.Groups["message"].Value)
            : (null, line);
    }

    /// <summary>
    /// Digs the resource id out of an <c>events</c> record's <c>Event</c> field. That field is an
    /// <b>escaped JSON string</b>, not an object, and it is the only place the id appears: the record's
    /// own top-level <c>ID</c> was empty on every event captured
    /// (Notes/nerdctl-advanced-formats.md). Which key holds it depends on the topic — containerd events
    /// name it <c>id</c>, image events <c>name</c>, snapshot events <c>key</c> — so all three are tried
    /// in that order. Nothing readable comes back as an empty string rather than throwing: an event
    /// stream that dies on one unfamiliar topic is worse than an event with no id.
    /// </summary>
    public static string NestedId(string escapedJson)
    {
        if (string.IsNullOrWhiteSpace(escapedJson))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(escapedJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return string.Empty;

            foreach (var key in (string[])["id", "name", "key"])
            {
                if (document.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
