using System.Reflection;

namespace Kontena.App.Services;

/// <summary>
/// The version this build carries, in the one form every surface shows it.
/// <para>
/// It has to be the <em>informational</em> version. The assembly version is numeric only — four
/// integers — so <c>0.3.0-nightly.20260731.44</c> is stored as <c>0.3.0.0</c> and every screen that
/// read it claimed to be a stable 0.3.0. On a nightly that is not a cosmetic loss: the update card
/// then reads "0.3.0 → 0.3.0-nightly.20260731.44" after installing exactly that nightly, so a build
/// cannot be told apart from the one it was updated from.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>What this build calls itself, e.g. <c>0.3.0-nightly.20260731.44</c>.</summary>
    public static string Current { get; } = From(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion,
        typeof(AppVersion).Assembly.GetName().Version);

    /// <summary>
    /// The day the build workflow made this build (<c>2026-07-31</c>), or null for a build it did
    /// not make — a local <c>dotnet run</c> has no release date to claim.
    /// <para>
    /// It exists because the version no longer carries one. A nightly used to be
    /// <c>0.3.0-nightly.20260731.44</c>; the date made it long enough that nobody read it, and it was
    /// never what ordered two nightlies anyway — the run number already does that. So the date moved
    /// to where it is information instead of an ordering key (KON-268).
    /// </para>
    /// </summary>
    public static string? BuildDate { get; } = typeof(AppVersion).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")
        ?.Value is { Length: > 0 } date
        ? date
        : null;

    /// <summary>
    /// The build date as every surface phrases it — <c>built 2026-07-31</c> — or empty for a build
    /// that has none. One string rather than three, so About, Settings and the update card cannot
    /// end up wording the same fact differently.
    /// </summary>
    public static string BuiltOn { get; } = BuildDate is null ? string.Empty : $"built {BuildDate}";

    /// <param name="informational">
    /// The assembly's informational version, which is the string the build workflow stamped.
    /// </param>
    /// <param name="assemblyVersion">
    /// The numeric assembly version, used only when there is no informational one to read — which
    /// means a host that was not built by the workflow, not a build whose version went missing.
    /// </param>
    internal static string From(string? informational, Version? assemblyVersion)
    {
        // SourceLink appends "+<commit>". That is build metadata, not part of the version, and the
        // commit sha is not what someone comparing two builds is looking at.
        var version = informational?.Split('+', 2)[0];

        return string.IsNullOrWhiteSpace(version)
            ? assemblyVersion?.ToString(3) ?? "0.0.0"
            : version;
    }
}
