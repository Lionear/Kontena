namespace Kontena.Core.Models;

/// <summary>
/// Reading and writing whether the Containers list groups Compose projects, per backend (KON-159).
/// </summary>
/// <remarks>
/// Its own file for the same reason <c>ClusterVisibility</c> is: <see cref="KontenaSettings"/> is a
/// record of values, and the rules about one of those values are not values.
/// </remarks>
public static class ContainerGrouping
{
    /// <summary>
    /// Whether this backend's container list groups by Compose project. On unless someone turned it
    /// off — a flat list of a stack's containers is what the report was about, so the default has to
    /// be the grouped one.
    /// </summary>
    public static bool GroupsContainers(this KontenaSettings settings, string? backend)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return backend is not { Length: > 0 }
               || !settings.ContainerGrouping.TryGetValue(backend, out var grouped)
               || grouped;
    }

    /// <summary>
    /// Remember the choice for this backend. The "on" case is stored rather than removed: an explicit
    /// yes and an absent answer look the same today, but only one of them survives a change of default.
    /// </summary>
    public static KontenaSettings WithContainerGrouping(
        this KontenaSettings settings, string? backend, bool grouped)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (backend is not { Length: > 0 })
            return settings;

        var map = new Dictionary<string, bool>(settings.ContainerGrouping, StringComparer.Ordinal)
        {
            [backend] = grouped,
        };

        return settings with { ContainerGrouping = map };
    }
}
