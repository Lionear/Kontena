using System.Text.RegularExpressions;

namespace Kontena.Sdk.Orchestration.Provisioning;

/// <summary>
/// Whether a name can be used for a local cluster, and if not, why in a sentence someone can act on.
/// <para>
/// Checked here rather than left to the tool because the name lands in two places at once — a
/// container name (<c>&lt;name&gt;-control-plane</c>) and a kubeconfig context — and a create that
/// fails halfway can leave one of them behind. The form asks first; a rejection before anything starts
/// costs nothing.
/// </para>
/// </summary>
public static partial class LocalClusterName
{
    /// <summary>The longest name we accept. Node containers add a suffix, and the name is meant to be read.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// What is wrong with <paramref name="name"/>, or null when nothing is. Returns the reason rather
    /// than a bool so the message next to the field says which rule was broken.
    /// </summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Give the cluster a name.";

        if (name.Length > MaxLength)
            return $"Use at most {MaxLength} characters.";

        if (name.Any(char.IsUpper))
            return "Use lowercase only — a cluster name becomes a container name, which cannot hold capitals.";

        return Allowed().IsMatch(name)
            ? null
            : "Use lowercase letters, digits, dots and dashes, starting and ending with a letter or digit.";
    }

    /// <summary>Whether the name is usable as-is.</summary>
    public static bool IsValid(string? name) => Problem(name) is null;

    /// <summary>Throws when the name is unusable. For the provisioners, which take a spec they did not fill in.</summary>
    /// <exception cref="ArgumentException">The name cannot be used.</exception>
    public static void Validate(string? name, string parameterName)
    {
        if (Problem(name) is { } problem)
            throw new ArgumentException(problem, parameterName);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")]
    private static partial Regex Allowed();
}
