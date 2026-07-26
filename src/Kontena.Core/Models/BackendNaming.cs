namespace Kontena.Core.Models;

/// <summary>
/// Resolving what a backend is called (KON-119).
/// <para>
/// One place, because a backend's name is shown in six: the switcher, the title bar, the Settings list,
/// the onboarding list, and both "cannot reach it" messages. A rename that only reached some of them
/// would leave the user reading two names for the same thing and doubting which one they are on.
/// </para>
/// </summary>
public static class BackendNaming
{
    /// <summary>
    /// The name to show for a backend: what the user called it, or what the source calls itself.
    /// </summary>
    /// <param name="settings">Where the overrides live.</param>
    /// <param name="backend">Backend id.</param>
    /// <param name="fallback">The source's own name, used when there is no override.</param>
    public static string NameFor(this KontenaSettings settings, string backend, string fallback)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.BackendNames.TryGetValue(backend, out var chosen)
            && !string.IsNullOrWhiteSpace(chosen)
                ? chosen.Trim()
                : fallback;
    }

    /// <summary>
    /// Settings with this backend renamed, or with the override removed when the name is blank or is
    /// just the source's own name again. Storing a name identical to the fallback would freeze it: the
    /// backend would keep the old name after the source changed its own.
    /// </summary>
    public static KontenaSettings WithBackendName(
        this KontenaSettings settings, string backend, string? name, string fallback)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var names = new Dictionary<string, string>(settings.BackendNames, StringComparer.Ordinal);
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || string.Equals(trimmed, fallback, StringComparison.Ordinal))
            names.Remove(backend);
        else
            names[backend] = trimmed;

        return settings with { BackendNames = names };
    }

    /// <summary>
    /// Drops names for backends that are no longer there. A context removed from a kubeconfig leaves an
    /// entry behind, which is harmless once but not if the file only ever grows.
    /// </summary>
    public static KontenaSettings PruneBackendNames(
        this KontenaSettings settings, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(known);

        var alive = new HashSet<string>(known, StringComparer.Ordinal);
        if (settings.BackendNames.Keys.All(alive.Contains))
            return settings;

        return settings with
        {
            BackendNames = settings.BackendNames
                .Where(pair => alive.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }
}
