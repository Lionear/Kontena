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

    /// <summary>Human name for a <c>kontena.source</c> label value.</summary>
    public static string ManagedSource(string? source) => source switch
    {
        null or "" => "another tool",
        "sqlexplorer" => "SQL Explorer",
        _ => char.ToUpperInvariant(source[0]) + source[1..],
    };

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
