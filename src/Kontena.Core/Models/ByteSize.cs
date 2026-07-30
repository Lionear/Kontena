using System.Globalization;

namespace Kontena.Core.Models;

/// <summary>
/// Byte counts in the form the UI shows them. Lives here rather than in the app because the
/// diagnosis rules (KON-150) quote sizes in their evidence and run without a UI.
/// </summary>
public static class ByteSize
{
    /// <summary>Decimal units, the same ones the container and pod pages have always shown.</summary>
    public static string Format(long bytes)
    {
        double b = bytes;
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
}
