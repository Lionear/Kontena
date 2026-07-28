using System.Globalization;

namespace Kontena.App.ViewModels;

/// <summary>Small display formatters shared by the list pages.</summary>
internal static class Format
{
    public static string Size(long? bytes)
    {
        if (bytes is null)
            return "—";

        double b = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (b >= 1000 && u < units.Length - 1)
        {
            b /= 1000;
            u++;
        }

        var value = u == 0 ? b.ToString("0", CultureInfo.InvariantCulture)
                           : b.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{value} {units[u]}";
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
