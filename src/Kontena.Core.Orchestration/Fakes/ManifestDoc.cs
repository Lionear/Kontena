using System.Globalization;
using System.Text;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Core.Models;

namespace Kontena.Core.Orchestration.Fakes;

/// <summary>One container in a manifest's pod spec.</summary>
internal sealed record ManifestContainer(string Name, string Image);

/// <summary>One port entry in a Service spec.</summary>
internal sealed record ManifestPort(string Name, int Port, int TargetPort, string Protocol);

/// <summary>
/// The fake's neutral projection of a manifest: the handful of fields it models, in one shape.
/// <para>
/// Both sides of an apply are projected into this: the desired side by parsing the user's YAML,
/// the live side by reading the seeded world. Rendering both with <see cref="ToYaml"/> and diffing
/// the results means formatting, key order, and comments never read as changes — only the fields
/// that actually differ do, which is what a server-side dry-run reports.
/// </para>
/// <para>
/// A real adapter has no need for this — it round-trips through the Kubernetes client's own
/// serializer — so this type stays internal to the fake.
/// </para>
/// </summary>
internal sealed record ManifestDoc
{
    public string ApiVersion { get; init; } = "v1";
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? Namespace { get; init; }

    public int? Replicas { get; init; }
    public string? Schedule { get; init; }
    public string? ServiceType { get; init; }
    public string? ClusterIp { get; init; }
    public string? NodeName { get; init; }

    public IReadOnlyList<ManifestContainer> Containers { get; init; } = [];
    public IReadOnlyList<ManifestPort> Ports { get; init; } = [];
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Selector { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// A ConfigMap's or Secret's <c>data:</c>, base64 either way — the form the API stores and the
    /// only one that carries bytes (KON-422).
    /// <para>
    /// The fake modelled these two kinds as far as their key listing and stopped there: a manifest
    /// for one came back "not found", and applying one wrote nothing anybody could read back. Both
    /// halves of that are what a field editor needs to be testable at all.
    /// </para>
    /// <para>
    /// Null and empty are different. Null is "this document says nothing about data" — a Secret
    /// patched for its labels alone — and merges to what the cluster holds. Empty is "no keys",
    /// which is a real state an editor can produce by removing the last one.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    /// <summary>A Secret's <c>type:</c>, which sits at the top level rather than under a spec.</summary>
    public string? SecretType { get; init; }

    /// <summary>Pre-rendered <c>status:</c> lines, shown when reading a manifest but never applied.</summary>
    public IReadOnlyList<string> Status { get; init; } = [];

    /// <summary>
    /// For kinds the fake does not model (an HPA, a ConfigMap): the comment-stripped source, kept
    /// verbatim so a repeat apply of the same document still reports "unchanged".
    /// </summary>
    public string? Raw { get; init; }

    /// <summary>Set when the document could not be parsed; drives <c>ApplyAction.Failed</c>.</summary>
    public string? Error { get; init; }

    /// <summary>Workload kinds carry their containers under <c>spec.template.spec</c>.</summary>
    private bool HasPodTemplate => Kind is "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "Job";

    public ResourceRef ToRef()
    {
        var slash = ApiVersion.LastIndexOf('/');
        var group = slash < 0 ? string.Empty : ApiVersion[..slash];
        var version = slash < 0 ? ApiVersion : ApiVersion[(slash + 1)..];
        return new ResourceRef(new GroupVersionKind(group, version, Kind), Namespace, Name);
    }

    /// <summary>
    /// Render the canonical YAML. <paramref name="includeStatus"/> adds the read-only status block
    /// (for the live-manifest editor); apply comparisons always leave it out, as kubectl does.
    /// </summary>
    public string ToYaml(bool includeStatus = false)
    {
        if (Raw is not null)
            return Raw;

        var sb = new StringBuilder();
        sb.Append("apiVersion: ").Append(ApiVersion).Append('\n');
        sb.Append("kind: ").Append(Kind).Append('\n');
        sb.Append("metadata:\n");
        sb.Append("  name: ").Append(Name).Append('\n');
        if (Namespace is not null)
            sb.Append("  namespace: ").Append(Namespace).Append('\n');
        if (Labels.Count > 0)
        {
            sb.Append("  labels:\n");
            foreach (var (k, v) in Labels.OrderBy(l => l.Key, StringComparer.Ordinal))
                sb.Append("    ").Append(k).Append(": ").Append(v).Append('\n');
        }

        if (SecretType is not null)
            sb.Append("type: ").Append(SecretType).Append('\n');

        // Straight after metadata, where kubectl puts it: data is a top-level field on these kinds,
        // not part of spec.
        if (Data is { } data)
        {
            sb.Append("data:\n");
            foreach (var (k, v) in data.OrderBy(d => d.Key, StringComparer.Ordinal))
                sb.Append("  ").Append(k).Append(": ").Append(v).Append('\n');
        }

        var spec = new StringBuilder();
        if (Replicas is { } replicas)
            spec.Append("  replicas: ").Append(replicas.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (Schedule is not null)
            spec.Append("  schedule: \"").Append(Schedule).Append("\"\n");
        if (ServiceType is not null)
            spec.Append("  type: ").Append(ServiceType).Append('\n');
        if (ClusterIp is not null)
            spec.Append("  clusterIP: ").Append(ClusterIp).Append('\n');
        if (NodeName is not null)
            spec.Append("  nodeName: ").Append(NodeName).Append('\n');
        if (Selector.Count > 0)
        {
            spec.Append("  selector:\n");
            foreach (var (k, v) in Selector.OrderBy(s => s.Key, StringComparer.Ordinal))
                spec.Append("    ").Append(k).Append(": ").Append(v).Append('\n');
        }

        if (Ports.Count > 0)
        {
            spec.Append("  ports:\n");
            foreach (var p in Ports)
            {
                spec.Append("    - name: ").Append(p.Name).Append('\n');
                spec.Append("      port: ").Append(p.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
                spec.Append("      targetPort: ").Append(p.TargetPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
                spec.Append("      protocol: ").Append(p.Protocol).Append('\n');
            }
        }

        if (Containers.Count > 0)
        {
            var indent = HasPodTemplate ? "        " : "    ";
            if (HasPodTemplate)
                spec.Append("  template:\n    spec:\n      containers:\n");
            else
                spec.Append("  containers:\n");

            foreach (var c in Containers)
            {
                spec.Append(indent).Append("- name: ").Append(c.Name).Append('\n');
                spec.Append(indent).Append("  image: ").Append(c.Image).Append('\n');
            }
        }

        if (spec.Length > 0)
            sb.Append("spec:\n").Append(spec);

        if (includeStatus && Status.Count > 0)
        {
            sb.Append("status:\n");
            foreach (var line in Status)
                sb.Append("  ").Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
