using System.Globalization;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Adapters.LocalClusters;

/// <summary>
/// Reads <c>minikube config defaults kubernetes-version</c> (KON-144).
/// <para>
/// The output is a bullet per version, newest first, and it is long — every patch of every minor it
/// has ever supported, alphas and release candidates included. Offering that raw would be a list
/// nobody scrolls; offering a list we typed ourselves goes stale the moment minikube updates. So it is
/// narrowed here, by rules rather than by hand.
/// </para>
/// </summary>
public static class MinikubeVersions
{
    /// <summary>How many minors to keep. Four covers the supported window with room to spare.</summary>
    private const int Minors = 4;

    /// <summary>
    /// The versions worth offering: stable releases only, the newest patch of each of the newest few
    /// minors, newest first. The first one is also minikube's default, because this list is what it
    /// picks from when asked for no version in particular.
    /// </summary>
    public static ClusterVersionOptions Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return ClusterVersionOptions.None;

        var parsed = new List<(int Major, int Minor, int Patch)>();

        foreach (var line in output.Split('\n'))
        {
            // Bullets, but not necessarily: the shape of a list is not a contract either.
            var text = line.Trim().TrimStart('*', '-', ' ').Trim();

            if (Number(text) is { } version)
                parsed.Add(version);
        }

        // Pre-releases are dropped rather than sorted below the stables: an alpha in a create form is a
        // trap, and someone who genuinely wants one can still type it in the node image field.
        var offered = parsed
            .GroupBy(v => (v.Major, v.Minor))
            .OrderByDescending(g => g.Key.Major).ThenByDescending(g => g.Key.Minor)
            .Take(Minors)
            .Select(g => string.Create(
                CultureInfo.InvariantCulture, $"v{g.Key.Major}.{g.Key.Minor}.{g.Max(v => v.Patch)}"))
            .ToList();

        return offered.Count == 0 ? ClusterVersionOptions.None : new ClusterVersionOptions(offered, offered[0]);
    }

    /// <summary>
    /// A plain <c>vMAJOR.MINOR.PATCH</c>, or null for anything else — a heading, a blank line, or a
    /// pre-release like <c>v1.35.0-rc.1</c>, which is what the suffix check is really for.
    /// </summary>
    private static (int Major, int Minor, int Patch)? Number(string text)
    {
        if (!text.StartsWith('v'))
            return null;

        var parts = text[1..].Split('.');
        if (parts.Length != 3)
            return null;

        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
               && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? (major, minor, patch)
            : null;
    }
}
