using System.Globalization;
using Kontena.Core.Models;

namespace Kontena.App.ViewModels;

/// <summary>Small display formatters shared by the list pages.</summary>
internal static class Format
{
    /// <summary>
    /// The formatting itself lives in <see cref="ByteSize"/> since KON-150: the diagnosis rules quote
    /// sizes without a UI, and two copies of this would eventually disagree about what a megabyte is.
    /// </summary>
    public static string Size(long? bytes) => bytes is null ? "—" : ByteSize.Format(bytes.Value);

    /// <summary>
    /// Milli-cores as whole cores, for a total big enough that "32000m" reads as noise (KON-378).
    /// Per-node figures stay in milli-cores, the unit the node itself is stated in; this is only for
    /// sums across a cluster, where a fraction of a core is rounding rather than information — hence
    /// one decimal, and none at all when it lands on a whole core.
    /// </summary>
    public static string Cores(long millicores)
    {
        var cores = millicores / 1000d;
        return Math.Abs(cores - Math.Round(cores)) < 0.05
            ? Math.Round(cores).ToString("0", CultureInfo.InvariantCulture)
            : cores.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A storage quantity the way Kubernetes states it — binary units with the <c>Gi</c> suffix
    /// (KON-247).
    /// <para>
    /// Deliberately not <see cref="Size"/>. That one is decimal, which is right for images and
    /// container memory and is what every engine reports, but a claim written as <c>20Gi</c> would
    /// then render as "21.5 GB" — beside a <c>kubectl get pvc</c> saying <c>20Gi</c> that reads as
    /// Kontena being wrong about the number. The quantity string itself does not survive into the
    /// model, so this reconstructs the form it was almost certainly written in.
    /// </para>
    /// </summary>
    public static string Quantity(long bytes)
    {
        if (bytes <= 0)
            return "—";

        string[] units = ["", "Ki", "Mi", "Gi", "Ti", "Pi"];

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // A whole number of Gi is the normal case and deserves "20Gi", not "20.0Gi"; anything else
        // keeps one decimal so a 1.5Gi claim does not round to its neighbour.
        var text = Math.Abs(value - Math.Round(value)) < 0.05
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);

        return text + units[unit];
    }

    /// <summary>
    /// Human name for a <c>kontena.source</c> label value. The fallback capitalises the raw value, which
    /// is right for a one-word tool and wrong for anything else — hence the map. <c>sqlexplorer</c> is
    /// DataTray's former name and must stay: a container carries the label it was created with for as
    /// long as it lives, so renaming the product does not relabel what is already running.
    /// </summary>
    public static string ManagedSource(string? source) => source switch
    {
        null or "" => "another tool",
        "datatray" => "DataTray",
        "sqlexplorer" => "SQL Explorer",
        _ => char.ToUpperInvariant(source[0]) + source[1..],
    };

    /// <summary>Compact, k8s-style age from a duration, e.g. "9d", "30h", "12m", "45s".</summary>
    public static string Duration(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h";
        if (age.TotalMinutes >= 1) return $"{(int)age.TotalMinutes}m";
        return $"{(int)age.TotalSeconds}s";
    }

    public static string Age(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} hours ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays} days ago";
        if (delta.TotalDays < 365) return $"{(int)(delta.TotalDays / 30)} months ago";
        return $"{(int)(delta.TotalDays / 365)} years ago";
    }
}
