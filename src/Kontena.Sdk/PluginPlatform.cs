namespace Kontena.Sdk;

/// <summary>
/// One operating system a plugin runs on, and the oldest version of it that will do (KON-280).
/// <para>
/// A plugin lists these so the host can decide <em>without loading it</em> whether it belongs on this
/// machine — in the wizard, in the store, and in the loader. Without the declaration the host would
/// have to know per plugin what it is, which is exactly the knowledge a plugin model exists to remove.
/// </para>
/// <para>
/// The minimum version is per operating system rather than one number for the plugin, because that is
/// how the requirement actually falls: Apple's <c>container</c> needs macOS 26, and a plugin that also
/// speaks to Linux has no version floor there at all. One shared number could only be wrong for one of
/// them.
/// </para>
/// </summary>
public sealed record PluginPlatform
{
    /// <summary>
    /// The operating system, as .NET names it: <c>windows</c>, <c>linux</c>, <c>macos</c>. Matching is
    /// case-insensitive, so <c>macOS</c> reads the same.
    /// <para>
    /// A string rather than an enum, and deliberately: this is the same vocabulary
    /// <see cref="OperatingSystem.IsOSPlatform"/> and <c>[SupportedOSPlatform]</c> already use, and the
    /// set of operating systems is not ours to close. A name this build does not know simply does not
    /// match — which is the right answer for a plugin written for a platform we do not run on, and
    /// leaves a typo visible in the rejection rather than swallowed as "runs anywhere".
    /// </para>
    /// </summary>
    public required string Os { get; init; }

    /// <summary>
    /// The oldest version of <see cref="Os"/> the plugin works on — <c>26</c>, <c>26.0</c> or
    /// <c>10.0.19041</c> — or empty for no floor, which is the common case.
    /// </summary>
    public string MinVersion { get; init; } = string.Empty;

    /// <summary>
    /// Whether this machine is the operating system named here, at or above <see cref="MinVersion"/>.
    /// <para>
    /// A version that cannot be read counts as "no", not as "no floor": a manifest that means macOS 26
    /// and mistypes it must not end up running on macOS 13. <see cref="ToString"/> puts the text back in
    /// the rejection, so the typo is what the reader sees.
    /// </para>
    /// </summary>
    public bool MatchesHost()
    {
        if (MinVersion.Length == 0)
            return OperatingSystem.IsOSPlatform(Os);

        // Version wants two parts at minimum, and "macOS 26" is how a person writes a major-only floor.
        var text = MinVersion.Contains('.', StringComparison.Ordinal) ? MinVersion : MinVersion + ".0";

        return Version.TryParse(text, out var min)
               && OperatingSystem.IsOSPlatformVersionAtLeast(Os, min.Major, min.Minor, Math.Max(min.Build, 0));
    }

    /// <summary>
    /// Whether a plugin declaring <paramref name="platforms"/> may run on this machine.
    /// <para>
    /// An empty list means "anywhere". That default is what keeps the field from being noise: a plugin
    /// that is pure managed code has no platform opinion, and forcing its author to enumerate three
    /// operating systems they never tested would make the declaration less trustworthy, not more. The
    /// rule lives here rather than at each call site because the store and the wizard have to read the
    /// same manifest the same way the loader does.
    /// </para>
    /// </summary>
    public static bool SupportsHost(IReadOnlyCollection<PluginPlatform> platforms) =>
        platforms.Count == 0 || platforms.Any(p => p.MatchesHost());

    /// <summary>What a rejection or a store listing shows: "linux", "macos 26".</summary>
    public override string ToString() => MinVersion.Length == 0 ? Os : Os + " " + MinVersion;
}
