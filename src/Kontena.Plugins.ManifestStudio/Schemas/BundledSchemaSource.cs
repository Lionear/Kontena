using System.Reflection;

namespace Kontena.Plugins.ManifestStudio.Schemas;

/// <summary>
/// The groups actually bundled, and which embedded file each one lives in. See
/// <c>Resources/Schemas/README.md</c> for where the data came from and how it was trimmed.
/// </summary>
internal static class BundledSchemas
{
    /// <summary>Bundled minors, most recent first — the one <see cref="BundledSchemaSource"/> picks
    /// by default.</summary>
    public static readonly IReadOnlyList<string> Minors = ["1.36", "1.35", "1.34"];

    public static readonly IReadOnlyDictionary<(string Group, string Version), string> FileNames =
        new Dictionary<(string, string), string>
        {
            [(string.Empty, "v1")] = "core_v1.json",
            [("apps", "v1")] = "apps_v1.json",
            [("batch", "v1")] = "batch_v1.json",
            [("networking.k8s.io", "v1")] = "networking_v1.json",
            [("rbac.authorization.k8s.io", "v1")] = "rbac_v1.json",
            [("autoscaling", "v2")] = "autoscaling_v2.json",
            [("policy", "v1")] = "policy_v1.json",
            [("storage.k8s.io", "v1")] = "storage_v1.json",
        };
}

/// <summary>
/// The offline fallback (KON-289, Plan §3): "gebundelde upstream-set per minor" for when no cluster is
/// connected at all. Real Kubernetes OpenAPI documents, vendored and trimmed — never a substitute for
/// asking an actual cluster, and never used to override one that answered.
/// <para>
/// <see cref="Banner"/> is Plan §3's requirement made literal: this source can never know about a
/// custom resource, and can never say whether a served apiVersion has since been removed from any real
/// cluster — only <c>ClusterEngineSchemaSource</c> can answer either question.
/// </para>
/// </summary>
public sealed class BundledSchemaSource(string? minor = null) : IClusterSchemaSource
{
    private readonly string _minor = minor ?? BundledSchemas.Minors[0];
    private readonly Assembly _assembly = typeof(BundledSchemaSource).Assembly;

    public const string Banner =
        "No cluster connected — showing bundled upstream Kubernetes schemas. Custom resources are "
        + "unknown, and a removed apiVersion will not be flagged: only a real cluster can answer either.";

    public ValueTask<string> GetServerVersionAsync(CancellationToken ct = default) =>
        ValueTask.FromResult($"bundled-{_minor}");

    public ValueTask<string?> GetOpenApiSchemaAsync(string group, string version, CancellationToken ct = default)
    {
        if (!BundledSchemas.FileNames.TryGetValue((group, version), out var fileName))
            return ValueTask.FromResult<string?>(null);

        var suffix = $".v{_minor.Replace('.', '_')}.{fileName}";
        var resourceName = _assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));

        if (resourceName is null)
            return ValueTask.FromResult<string?>(null);

        using var stream = _assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return ValueTask.FromResult<string?>(reader.ReadToEnd());
    }
}
