using System.Globalization;

namespace Kontena.Core.Orchestration;

/// <summary>
/// A Kubernetes version reduced to what the skew policy cares about: major and minor. Patch,
/// pre-release suffixes and vendor suffixes (<c>v1.29.4-gke.1043000</c>, <c>v1.30.0+k3s1</c>) are
/// deliberately dropped — the version skew policy is expressed purely in minor versions.
/// </summary>
public readonly record struct KubernetesVersion(int Major, int Minor) : IComparable<KubernetesVersion>
{
    /// <summary>
    /// Parse an apiserver or kubelet version string. Returns null for anything that does not start
    /// with a <c>major.minor</c> pair — an unparseable version must read as "unknown" rather than be
    /// guessed at, because every conclusion downstream is a claim about a cluster's health.
    /// </summary>
    public static KubernetesVersion? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
            return null;

        if (!int.TryParse(text[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            return null;

        // The minor runs until the next separator: '.' for the patch, but also '-' and '+' for the
        // vendor builds that skip the patch entirely (v1.30+k3s1).
        var rest = text[(dot + 1)..];
        var end = rest.AsSpan().IndexOfAny('.', '-', '+');
        var minorText = end < 0 ? rest : rest[..end];

        return int.TryParse(minorText, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? new KubernetesVersion(major, minor)
            : null;
    }

    public int CompareTo(KubernetesVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(KubernetesVersion left, KubernetesVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(KubernetesVersion left, KubernetesVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(KubernetesVersion left, KubernetesVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(KubernetesVersion left, KubernetesVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"v{Major}.{Minor}";
}

/// <summary>How a node's kubelet sits relative to the apiserver it is registered with.</summary>
public enum VersionSkewState
{
    /// <summary>One of the two versions could not be read — say nothing rather than guess.</summary>
    Unknown,

    /// <summary>Inside the supported window (equal, or trailing by an allowed number of minors).</summary>
    Supported,

    /// <summary>Trailing the apiserver by more minors than the policy allows.</summary>
    Outdated,

    /// <summary>Newer than the apiserver — never supported, in any direction.</summary>
    Ahead,
}

/// <summary>The verdict for one node, ready to render.</summary>
/// <param name="State">Which side of the policy this node falls on.</param>
/// <param name="MinorsBehind">
/// How many minor versions the kubelet trails the apiserver by; negative when it is ahead. Only
/// meaningful when both versions parsed and share a major.
/// </param>
/// <param name="Summary">Short label for a chip.</param>
/// <param name="Detail">The sentence behind it — what is wrong and what it means.</param>
public sealed record NodeVersionSkew(VersionSkewState State, int MinorsBehind, string Summary, string Detail)
{
    /// <summary>Whether this is worth putting in front of the user.</summary>
    public bool IsProblem => State is VersionSkewState.Outdated or VersionSkewState.Ahead;
}

/// <summary>
/// The Kubernetes <a href="https://kubernetes.io/releases/version-skew-policy/">version skew
/// policy</a>, applied to the two numbers Kontena already holds: the apiserver version and each
/// node's kubelet version (KON-68).
///
/// <para>This needs no external data and no network — it is a comparison, so it is always correct
/// and never goes stale. Whether the release itself is still supported upstream is a different
/// question with a different answer, and is deliberately not answered here (KON-95 part 2).</para>
///
/// <para>It catches a real and common failure: a cluster upgrade where the control plane moved and
/// some nodes were left behind.</para>
/// </summary>
public static class VersionSkewPolicy
{
    /// <summary>
    /// How many minor versions a kubelet may trail its apiserver. Kubernetes widened this from 2 to
    /// 3 in 1.28, so the answer depends on the control plane doing the allowing.
    /// </summary>
    public static int SupportedMinorLag(KubernetesVersion apiServer) =>
        apiServer.Major > 1 || (apiServer.Major == 1 && apiServer.Minor >= 28) ? 3 : 2;

    /// <summary>Compare one node's kubelet against the apiserver.</summary>
    public static NodeVersionSkew Evaluate(string? apiServerVersion, string? kubeletVersion)
    {
        var api = KubernetesVersion.Parse(apiServerVersion);
        var kubelet = KubernetesVersion.Parse(kubeletVersion);

        if (api is not { } server || kubelet is not { } node)
        {
            return new NodeVersionSkew(
                VersionSkewState.Unknown, 0, "Version unknown",
                "Kontena could not read the apiserver or kubelet version, so it cannot say whether they are compatible.");
        }

        if (node > server)
        {
            var behind = server.Minor - node.Minor;
            return new NodeVersionSkew(
                VersionSkewState.Ahead, behind, "Kubelet ahead of apiserver",
                $"The kubelet ({node}) is newer than the apiserver ({server}). Kubernetes does not support a kubelet " +
                "ahead of the control plane in any configuration — upgrade the control plane before the nodes.");
        }

        var lag = SupportedMinorLag(server);

        // A whole major behind is outside any window, and subtracting minors across majors would be
        // meaningless — Kubernetes has never released a 2.x, but the model should not lie if it does.
        if (node.Major != server.Major)
        {
            return new NodeVersionSkew(
                VersionSkewState.Outdated, 0, "Kubelet unsupported",
                $"The kubelet ({node}) is a major version behind the apiserver ({server}), far outside the supported " +
                $"window of {lag} minor versions.");
        }

        var minorsBehind = server.Minor - node.Minor;
        if (minorsBehind <= lag)
        {
            return new NodeVersionSkew(
                VersionSkewState.Supported, minorsBehind, "Kubelet supported",
                minorsBehind == 0
                    ? $"The kubelet ({node}) matches the apiserver."
                    : $"The kubelet ({node}) trails the apiserver ({server}) by {Minors(minorsBehind)}, within the " +
                      $"supported window of {lag}.");
        }

        return new NodeVersionSkew(
            VersionSkewState.Outdated, minorsBehind, $"Kubelet {Minors(minorsBehind)} behind",
            $"The kubelet ({node}) trails the apiserver ({server}) by {Minors(minorsBehind)}, more than the {lag} " +
            "Kubernetes supports. This node may behave incorrectly; it usually means a cluster upgrade that stopped " +
            "halfway.");
    }

    private static string Minors(int count) => count == 1 ? "1 minor version" : $"{count} minor versions";
}
