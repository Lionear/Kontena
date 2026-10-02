using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;
using Kontena.Sdk.Tooling;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// <see cref="IHelmReleases"/> by shelling out to <c>helm</c> (KON-473), pointed at the engine's own
/// kube-context and kubeconfig so it never acts on whatever the terminal's current context happens to be.
/// <para>
/// The CLI rather than reading the <c>sh.helm.release.v1.*</c> Secrets: listing could be done that way,
/// but rollback and uninstall could not, and helm already knows every storage driver.
/// </para>
/// <para>
/// Positionals go after <c>--</c> and option values are passed as <c>--flag=value</c>, so a release, a
/// chart or a version that starts with "-" stays a value and is never read as one of helm's options —
/// the concern <c>HelmArguments</c> handles for the renderer (KON-182).
/// </para>
/// </summary>
internal sealed partial class HelmCli(IToolRunner runner, Func<string> context, string? kubeconfigPath) : IHelmReleases
{
    public async ValueTask<IReadOnlyList<HelmRelease>> ListAsync(string? ns = null, CancellationToken ct = default)
    {
        string[] scope = ns is null ? ["--all-namespaces"] : [$"--namespace={ns}"];
        var json = await ReadAsync(["list", .. scope, "--all", "--output=json"], ct).ConfigureAwait(false);

        return Read(json, e => new HelmRelease
        {
            Name = String(e, "name"),
            Namespace = String(e, "namespace"),
            Chart = SplitChart(String(e, "chart")).Name,
            ChartVersion = SplitChart(String(e, "chart")).Version,
            AppVersion = String(e, "app_version"),
            Status = String(e, "status"),
            Revision = Int(e, "revision"),
            Updated = ParseTime(String(e, "updated")),
        });
    }

    public async ValueTask<string> GetValuesAsync(string release, string ns, bool all = false, CancellationToken ct = default)
    {
        string[] args = all
            ? ["get", "values", $"--namespace={ns}", "--all", "--output=yaml", "--", release]
            : ["get", "values", $"--namespace={ns}", "--output=yaml", "--", release];

        var yaml = (await ReadAsync(args, ct).ConfigureAwait(false)).Trim();

        // A release installed without any values reads back as the YAML literal "null".
        return yaml == "null" ? string.Empty : yaml;
    }

    public async ValueTask<string> GetManifestAsync(string release, string ns, CancellationToken ct = default) =>
        (await ReadAsync(["get", "manifest", $"--namespace={ns}", "--", release], ct).ConfigureAwait(false)).Trim();

    public async ValueTask<IReadOnlyList<HelmRevision>> GetHistoryAsync(string release, string ns, CancellationToken ct = default)
    {
        var json = await ReadAsync(["history", $"--namespace={ns}", "--output=json", "--", release], ct).ConfigureAwait(false);

        return Read(json, e => new HelmRevision
        {
            Revision = Int(e, "revision"),
            Status = String(e, "status"),
            Chart = String(e, "chart"),
            AppVersion = String(e, "app_version"),
            Description = String(e, "description"),
            Updated = ParseTime(String(e, "updated")),
        });
    }

    public async ValueTask<string?> UpgradeAsync(HelmUpgrade upgrade, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upgrade.Chart))
            return "Choose the chart to upgrade to: repo/chart, a path, or an oci:// reference.";

        // helm reads values from a file; the file is ours and gone again when the upgrade returns.
        var valuesFile = Path.Combine(Path.GetTempPath(), $"kontena-helm-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(valuesFile, upgrade.ValuesYaml, ct).ConfigureAwait(false);
        try
        {
            List<string> args = ["upgrade", $"--namespace={upgrade.Namespace}", $"--values={valuesFile}"];
            if (upgrade.Version.Trim().Length > 0)
                args.Add($"--version={upgrade.Version.Trim()}");
            args.AddRange(["--", upgrade.Release, upgrade.Chart.Trim()]);

            return await WriteAsync(args, ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(valuesFile);
        }
    }

    public ValueTask<string?> RollbackAsync(string release, string ns, int revision, CancellationToken ct = default) =>
        WriteAsync(["rollback", $"--namespace={ns}", "--", release, revision.ToString(CultureInfo.InvariantCulture)], ct);

    public ValueTask<string?> UninstallAsync(string release, string ns, CancellationToken ct = default) =>
        WriteAsync(["uninstall", $"--namespace={ns}", "--", release], ct);

    /// <summary>Run a read; a failure is thrown so the page shows it instead of an empty list.</summary>
    private async ValueTask<string> ReadAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunAsync(args, ct).ConfigureAwait(false);
        return result.Ok ? result.StandardOutput : throw new InvalidOperationException(result.Complaint);
    }

    /// <summary>Run a write: null when it went through, otherwise helm's own words.</summary>
    private async ValueTask<string?> WriteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(args, ct).ConfigureAwait(false);
            return result.Ok ? null : result.Complaint;
        }
        catch (ToolNotFoundException)
        {
            return "'helm' was not found on PATH. Install Helm to manage releases.";
        }
    }

    private ValueTask<ToolResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // Global flags first, before the "--" the command's own positionals sit behind.
        List<string> all = [$"--kube-context={context()}"];
        if (kubeconfigPath is not null)
            all.Add($"--kubeconfig={kubeconfigPath}");
        all.AddRange(args);

        return runner.RunAsync(new ToolInvocation(KnownTools.Helm, all), ct);
    }

    /// <summary>
    /// helm writes chart and version as one string. The version starts at the first "-" followed by a
    /// digit and a dot, which keeps names like <c>k8s-dashboard</c> whole and pre-release tails attached.
    /// </summary>
    internal static (string Name, string Version) SplitChart(string chart) =>
        ChartVersion().Match(chart) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : (chart, string.Empty);

    /// <summary>
    /// helm's timestamps come in two Go layouts — <c>2024-05-01 10:11:12.123456789 +0200 CEST</c> from
    /// list and RFC 3339 from history — both with more fractional digits than .NET parses.
    /// </summary>
    internal static DateTimeOffset? ParseTime(string text)
    {
        var m = GoTime().Match(text);
        if (!m.Success)
            return null;

        var fraction = m.Groups[3].Value is { Length: > 0 } f ? "." + f[..Math.Min(f.Length, 7)] : string.Empty;
        var offset = m.Groups[4].Value switch
        {
            "Z" => "+00:00",
            var o when o.Contains(':') => o,
            var o => o[..3] + ":" + o[3..],
        };

        return DateTimeOffset.TryParse(
            $"{m.Groups[1].Value}T{m.Groups[2].Value}{fraction}{offset}",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    }

    [GeneratedRegex(@"^(.+?)-(v?\d+\.\d.*)$")]
    private static partial Regex ChartVersion();

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d)[ T](\d\d:\d\d:\d\d)(?:\.(\d+))?\s*(Z|[+-]\d\d:?\d\d)")]
    private static partial Regex GoTime();

    private static IReadOnlyList<T> Read<T>(string json, Func<JsonElement, T> map)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? [.. document.RootElement.EnumerateArray().Select(map)]
            : [];
    }

    private static string String(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    /// <summary><c>helm list</c> writes the revision as a string, <c>helm history</c> as a number.</summary>
    private static int Int(JsonElement e, string property) =>
        !e.TryGetProperty(property, out var v) ? 0
        : v.ValueKind == JsonValueKind.Number ? v.GetInt32()
        : int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
