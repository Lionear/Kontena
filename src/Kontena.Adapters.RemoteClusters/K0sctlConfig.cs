using System.Globalization;
using System.Text;
using Kontena.Sdk.Orchestration.Provisioning;

namespace Kontena.Adapters.RemoteClusters;

/// <summary>
/// Writes the <c>k0sctl.yaml</c> a <see cref="RemoteClusterSpec"/> describes (KON-236).
/// <para>
/// This one file is the whole cluster — every machine, its role, its login, and the network — which is
/// why k0s is the first of the three distributions. kubeadm needs a decision per node and Talos needs
/// a machine config per node; here there is one document, and it is the same document a user could
/// have written by hand and can read before it runs.
/// </para>
/// <para>
/// Written by hand rather than through a YAML library, which is this solution's standing choice —
/// nothing in it takes a YAML dependency, not even the manifest editor. It is also the counterpart of
/// <c>K0sctlImport</c> from KON-233, which reads the same file for the same reason.
/// </para>
/// </summary>
public static class K0sctlConfig
{
    /// <summary>What k0sctl calls the config this produces.</summary>
    public const string ApiVersion = "k0sctl.k0sproject.io/v1beta1";

    /// <summary>
    /// The config for <paramref name="spec"/>, ready to hand to <c>k0sctl apply</c>.
    /// </summary>
    /// <param name="spec">The cluster. Its hosts, roles, network and endpoint all land here.</param>
    /// <param name="credentials">
    /// The cluster's SSH credentials. Each host's own user and key win over these, through
    /// <see cref="SshCredentials.For"/> — one login for the fleet with a way out per machine (KON-234).
    /// </param>
    /// <exception cref="ArgumentException">The spec cannot be rolled out; the message says why.</exception>
    public static string Write(RemoteClusterSpec spec, SshCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(credentials);

        // Refused here rather than by k0sctl, which would report it after connecting to machines —
        // and a cluster with no controller is a mistake worth catching before anything is touched.
        if (spec.Problem() is { } problem)
            throw new ArgumentException(problem, nameof(spec));

        var yaml = new StringBuilder()
            .Append("apiVersion: ").AppendLine(ApiVersion)
            .AppendLine("kind: Cluster")
            .AppendLine("metadata:")
            .Append("  name: ").AppendLine(Scalar(spec.Name))
            .AppendLine("spec:")
            .AppendLine("  hosts:");

        foreach (var host in spec.Hosts)
            AppendHost(yaml, host, credentials.For(host));

        AppendK0s(yaml, spec);

        return yaml.ToString();
    }

    private static void AppendHost(StringBuilder yaml, RemoteClusterHost host, SshCredentials login)
    {
        yaml.AppendLine("    - ssh:")
            .Append("        address: ").AppendLine(Scalar(host.Address));

        if (login.User is { Length: > 0 } user)
            yaml.Append("        user: ").AppendLine(Scalar(user));

        // Omitted when the agent holds the key: k0sctl falls back to the agent exactly as ssh does,
        // and writing an empty keyPath would be a path it then fails to find.
        if (login.KeyPath is { Length: > 0 } key)
            yaml.Append("        keyPath: ").AppendLine(Scalar(key));

        yaml.Append("      role: ").AppendLine(Role(host.Role));

        if (host.NodeName is { Length: > 0 } name)
            yaml.Append("      hostname: ").AppendLine(Scalar(name));
    }

    private static void AppendK0s(StringBuilder yaml, RemoteClusterSpec spec)
    {
        var network = spec.PodCidr is { Length: > 0 }
                      || spec.ServiceCidr is { Length: > 0 }
                      || spec.Cni is { Length: > 0 };

        if (spec.KubernetesVersion is not { Length: > 0 } && !network && spec.ControlPlaneEndpoint is not { Length: > 0 })
            return;

        yaml.AppendLine("  k0s:");

        // Left out entirely when not chosen, which is how k0sctl is told to install the latest stable
        // it knows of. Writing a version we guessed at would pin the cluster to our guess.
        if (spec.KubernetesVersion is { Length: > 0 } version)
            yaml.Append("    version: ").AppendLine(Scalar(version));

        if (!network && spec.ControlPlaneEndpoint is not { Length: > 0 })
            return;

        yaml.AppendLine("    config:")
            .AppendLine("      apiVersion: k0s.k0sproject.io/v1beta1")
            .AppendLine("      kind: ClusterConfig")
            .AppendLine("      metadata:")
            .Append("        name: ").AppendLine(Scalar(spec.Name))
            .AppendLine("      spec:");

        if (spec.ControlPlaneEndpoint is { Length: > 0 } endpoint)
        {
            // The address every kubeconfig and every joining node will carry. Split off any port: k0s
            // wants them as separate fields, and passing "host:6443" as the address produces a
            // certificate for a name with a colon in it.
            var (address, port) = SplitEndpoint(endpoint);

            yaml.AppendLine("        api:")
                .Append("          externalAddress: ").AppendLine(Scalar(address));

            if (port is { } number)
                yaml.Append("          port: ").AppendLine(number.ToString(CultureInfo.InvariantCulture));
        }

        if (!network)
            return;

        yaml.AppendLine("        network:");

        if (spec.PodCidr is { Length: > 0 } pods)
            yaml.Append("          podCIDR: ").AppendLine(Scalar(pods));

        if (spec.ServiceCidr is { Length: > 0 } services)
            yaml.Append("          serviceCIDR: ").AppendLine(Scalar(services));

        if (spec.Cni is { Length: > 0 } cni)
            yaml.Append("          provider: ").AppendLine(Scalar(Provider(cni)));
    }

    /// <summary>
    /// k0s's word for a CNI. It ships <c>kuberouter</c> and accepts <c>calico</c>; anything else is
    /// <c>custom</c>, which means "install it yourself" — passed through as given rather than
    /// translated, so an unknown name fails in k0s's own words instead of ours.
    /// </summary>
    internal static string Provider(string cni) => cni.Trim().ToLowerInvariant() switch
    {
        "kube-router" or "kuberouter" => "kuberouter",
        "calico" => "calico",
        var other => other,
    };

    /// <summary>The two roles a spec has, in k0sctl's spelling.</summary>
    private static string Role(ClusterHostRole role) =>
        role == ClusterHostRole.Controller ? "controller" : "worker";

    /// <summary>
    /// Splits <c>host:port</c>, leaving a bare host alone. IPv6 in brackets keeps its colons.
    /// </summary>
    internal static (string Address, int? Port) SplitEndpoint(string endpoint)
    {
        var value = endpoint.Trim();

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);

            return close > 0 && value.Length > close + 2 && value[close + 1] == ':'
                   && int.TryParse(value[(close + 2)..], CultureInfo.InvariantCulture, out var bracketed)
                ? (value[1..close], bracketed)
                : (value.Trim('[', ']'), null);
        }

        var colon = value.LastIndexOf(':');

        // More than one colon and no brackets is a bare IPv6 address, not a host and a port.
        if (colon <= 0 || value.IndexOf(':', StringComparison.Ordinal) != colon)
            return (value, null);

        return int.TryParse(value[(colon + 1)..], CultureInfo.InvariantCulture, out var port)
            ? (value[..colon], port)
            : (value, null);
    }

    /// <summary>
    /// A scalar, quoted only where it has to be. Readability matters here — this file is shown to the
    /// user before it runs — but a value that would change the document's shape gets quotes.
    /// </summary>
    internal static string Scalar(string value)
    {
        var plain = value.Length > 0
            && !value.StartsWith(' ') && !value.EndsWith(' ')
            && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or '~' or '+' or '@');

        return plain ? value : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
