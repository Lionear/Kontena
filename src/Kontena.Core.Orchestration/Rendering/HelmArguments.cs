namespace Kontena.Core.Orchestration.Rendering;

/// <summary>
/// Why a value cannot be handed to <c>helm</c>, or null (KON-182).
/// <para>
/// A process argument list stops a shell from interpreting anything, but it does not stop helm from
/// reading an argument as one of its own <b>options</b>. A chart of <c>--kubeconfig=/tmp/theirs</c>
/// is not a chart, it is a flag, and the render then runs against a cluster or a CA nobody chose.
/// The positional arguments — chart, release, repository name, repository URL, search term — are
/// where that actually bites; <c>--version</c> and <c>--set</c> are refused on the same rule because
/// a value that reads as a flag is a value someone else filled in wrong, and these fields are meant
/// to be filled from a catalogue later rather than only by hand.
/// </para>
/// <para>
/// One rule, several callers: the renderer, the repository commands, and the chart search — the same
/// arrangement <c>RemoteEngine.ArgumentProblem</c> has for ssh (KON-181).
/// </para>
/// </summary>
public static class HelmArguments
{
    /// <summary>The schemes a chart repository may be served over.</summary>
    private static readonly string[] RepositorySchemes = ["http://", "https://", "oci://"];

    /// <summary>Why <paramref name="value"/> cannot be a <paramref name="what"/>, or null.</summary>
    public static string? OptionLike(string what, string? value) =>
        value?.Trim() is { Length: > 0 } trimmed && trimmed.StartsWith('-')
            ? $"A {what} cannot start with \"-\". Helm would read it as one of its own options rather than a {what}."
            : null;

    /// <summary>Why this render cannot run, or null. Checked before helm is invoked.</summary>
    public static string? RenderProblem(
        string? chart, string? release, string? version, IEnumerable<string>? sets)
    {
        if (OptionLike("chart", chart) is { } chartProblem)
            return chartProblem;

        if (OptionLike("release name", release) is { } releaseProblem)
            return releaseProblem;

        if (OptionLike("chart version", version) is { } versionProblem)
            return versionProblem;

        foreach (var set in sets ?? [])
        {
            if (OptionLike("value override", set) is { } setProblem)
                return setProblem;
        }

        return null;
    }

    /// <summary>Why this repository cannot be added, or null.</summary>
    public static string? RepositoryProblem(string? name, string? url)
    {
        if (OptionLike("repository name", name) is { } nameProblem)
            return nameProblem;

        if (OptionLike("repository URL", url) is { } urlProblem)
            return urlProblem;

        var trimmed = url?.Trim() ?? string.Empty;
        if (trimmed.Length > 0
            && !RepositorySchemes.Any(s => trimmed.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            return "A repository URL must start with http://, https:// or oci://.";
        }

        return null;
    }

    /// <summary>Why this search cannot run, or null.</summary>
    public static string? SearchProblem(string? term) => OptionLike("search term", term);
}
