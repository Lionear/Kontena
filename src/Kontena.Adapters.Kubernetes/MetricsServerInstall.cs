using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// The metrics-server install Kontena can perform for a cluster that has none (KON-93).
/// <para>
/// The manifest is the upstream release, embedded rather than fetched. Two reasons: installing into a
/// cluster must not depend on reaching GitHub at that moment, and a file in the repository is one a
/// reviewer can read — a URL is a promise about something they cannot see. <see cref="Version"/> and
/// <see cref="Sha256"/> say which release it is, and a test asserts the embedded bytes match, so the
/// claim cannot drift from the content.
/// </para>
/// <para>
/// Upgrading is deliberately manual: drop the new <c>components.yaml</c> in, change three constants,
/// and the checksum test tells you whether you did it consistently.
/// </para>
/// </summary>
public static class MetricsServerInstall
{
    /// <summary>The upstream release this installs.</summary>
    public const string Version = "v0.9.0";

    /// <summary>SHA-256 of the embedded <c>components.yaml</c>, as published for <see cref="Version"/>.</summary>
    public const string Sha256 = "1cec29a5267809306a2c6ec74a3e449abbb705b4a8beed0c8a1963910f72c79b";

    /// <summary>Where the release came from, so the manifest can be checked against its source.</summary>
    public const string SourceUrl =
        "https://github.com/kubernetes-sigs/metrics-server/releases/download/" + Version + "/components.yaml";

    /// <summary>The image the deployment runs — worth naming in a dialog that installs it.</summary>
    public const string Image = "registry.k8s.io/metrics-server/metrics-server:" + Version;

    /// <summary>The flag a kubelet with a self-signed serving certificate needs.</summary>
    private const string InsecureFlag = "--kubelet-insecure-tls";

    /// <summary>The last of the upstream args; the flag goes after it so the list stays readable.</summary>
    private const string LastUpstreamArg = "- --metric-resolution=15s";

    private static readonly string ResourceName =
        $"Kontena.Adapters.Kubernetes.Resources.metrics-server-{Version}.yaml";

    /// <summary>
    /// The manifest to apply.
    /// <para>
    /// With <paramref name="insecureKubeletTls"/> the deployment gets <c>--kubelet-insecure-tls</c>.
    /// That is not a detail to leave to the user: on kind, minikube and plain kubeadm the kubelet
    /// serves a self-signed certificate, metrics-server refuses it, and the only symptom is a pod that
    /// never becomes ready — an install that reports success and produces no gauges.
    /// </para>
    /// </summary>
    public static string Manifest(bool insecureKubeletTls)
    {
        var yaml = ReadEmbedded();

        if (!insecureKubeletTls)
            return yaml;

        var index = yaml.IndexOf(LastUpstreamArg, StringComparison.Ordinal);
        if (index < 0)
        {
            // The pinned manifest is checked by test, so this can only mean someone dropped in a new
            // upstream file whose args changed. Failing loudly beats installing a metrics-server that
            // silently never becomes ready.
            throw new InvalidOperationException(
                $"The embedded metrics-server manifest no longer contains \"{LastUpstreamArg}\", so "
                + $"{InsecureFlag} cannot be placed. Check the args block after upgrading it.");
        }

        // Same indentation as the line it follows: this is a YAML sequence item, and two spaces out is
        // a different document.
        var lineStart = yaml.LastIndexOf('\n', index) + 1;
        var indent = yaml[lineStart..index];

        return yaml[..(index + LastUpstreamArg.Length)]
            + Environment.NewLine + indent + "- " + InsecureFlag
            + yaml[(index + LastUpstreamArg.Length)..];
    }

    /// <summary>
    /// Whether this cluster's kubelet is likely to serve a certificate metrics-server will not accept,
    /// judged by the context name.
    /// <para>
    /// A guess, and named as one: the real answer needs the kubelet's certificate, which is exactly
    /// what cannot be read before the install. kind and minikube are the two that ship this way and
    /// the two Kontena creates itself (KON-77/78), so they are the two worth defaulting for. The
    /// dialog says which way it went, so a wrong guess is visible rather than silent.
    /// </para>
    /// </summary>
    public static bool LikelyNeedsInsecureKubeletTls(string? context) =>
        context is { Length: > 0 } name
        && (name.StartsWith("kind-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("kind", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("minikube", StringComparison.OrdinalIgnoreCase));

    /// <summary>The embedded manifest, unmodified — what the checksum is taken over.</summary>
    public static string ReadEmbedded()
    {
        using var stream = typeof(MetricsServerInstall).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded manifest {ResourceName} is missing.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>SHA-256 of the embedded manifest, lower-case hex.</summary>
    public static string EmbeddedChecksum()
    {
        using var stream = typeof(MetricsServerInstall).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded manifest {ResourceName} is missing.");

        return Convert.ToHexStringLower(SHA256.HashData(ReadAll(stream)));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// What the install creates, for the confirmation. Read off the manifest rather than typed out, so
    /// the dialog cannot promise something the file does not contain.
    /// </summary>
    public static IReadOnlyList<string> Creates() =>
        [.. ReadEmbedded()
            .Split('\n')
            // Column zero only. Indented "kind:" lines are the ClusterRole a binding *refers to*, and
            // counting those said the install creates four ClusterRoles where it creates two — a
            // dialog overstating what it is about to do. Caught by the test below, not by reading.
            .Where(l => l.StartsWith("kind: ", StringComparison.Ordinal))
            .Select(l => l["kind: ".Length..].Trim())
            .GroupBy(kind => kind, StringComparer.Ordinal)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}")];
}
