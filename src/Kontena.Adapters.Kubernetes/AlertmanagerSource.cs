using System.Globalization;
using System.Text.Json;
using Kontena.Sdk.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes;

/// <summary>
/// Alerts from a real cluster, over <see cref="ApiProxyHttp"/>.
/// <para>
/// It speaks to both servers because the question a view asks spans both: instances and silences
/// come from Alertmanager, while rule state — pending, inactive, and whether a rule is even loaded —
/// only Prometheus knows. Either half may be missing and the other still works: no Prometheus costs
/// the pending section and the PromQL check, no Alertmanager costs the list.
/// </para>
/// <para>
/// Reads answer empty on failure, the way a list page needs them to. The two writes throw, because a
/// silence that quietly did nothing leaves someone believing the pager is off.
/// </para>
/// </summary>
internal sealed class AlertmanagerSource(
    ApiProxyHttp proxy, ServiceEndpoint? alertmanager, ServiceEndpoint? prometheus) : IAlertSource
{
    public string Name => alertmanager is null ? "none" : "alertmanager";

    /// <summary>Where the Alertmanager that answered lives, for the UI to name.</summary>
    public string? Location => alertmanager?.ToString();

    // ── Reading ──────────────────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<Alert>> ListAlertsAsync(CancellationToken ct = default)
    {
        var firing = alertmanager is null
            ? []
            : await FiringAsync(alertmanager, ct).ConfigureAwait(false);

        // Pending instances never reach Alertmanager — it is not told until the `for` has elapsed —
        // so the only place they exist is Prometheus' rule state.
        var pending = prometheus is null
            ? []
            : (await LoadedRulesAsync(prometheus, ct).ConfigureAwait(false)).Pending;

        return [.. firing, .. pending];
    }

    private async Task<IReadOnlyList<Alert>> FiringAsync(ServiceEndpoint endpoint, CancellationToken ct)
    {
        var response = await proxy.GetAsync(endpoint, "api/v2/alerts?silenced=true&inhibited=true", ct)
            .ConfigureAwait(false);

        if (!response.Ok || response.Json is not { ValueKind: JsonValueKind.Array } array)
            return [];

        var alerts = new List<Alert>();
        foreach (var item in array.EnumerateArray())
        {
            if (Strings(item, "labels") is not { Count: > 0 } labels)
                continue;

            var status = item.TryGetProperty("status", out var s) ? s : default;

            alerts.Add(new Alert
            {
                Labels = labels,
                Annotations = Strings(item, "annotations") ?? new Dictionary<string, string>(),
                State = AlertState.Firing,
                StartsAt = Time(item, "startsAt") ?? DateTimeOffset.UtcNow,
                EndsAt = Time(item, "endsAt"),
                Fingerprint = Text(item, "fingerprint") ?? string.Empty,
                GeneratorURL = Text(item, "generatorURL"),
                Receivers = ReceiverNames(item),
                SilencedBy = TextArray(status, "silencedBy"),
                InhibitedBy = TextArray(status, "inhibitedBy"),
            });
        }

        return alerts;
    }

    public async ValueTask<IReadOnlyList<AlertRule>> ListRulesAsync(CancellationToken ct = default) =>
        prometheus is null ? [] : (await LoadedRulesAsync(prometheus, ct).ConfigureAwait(false)).Rules;

    /// <summary>
    /// One read of <c>/api/v1/rules</c> gives both the loaded rules and the pending instances, so it
    /// is parsed once into both rather than fetched twice.
    /// </summary>
    private async Task<(IReadOnlyList<AlertRule> Rules, IReadOnlyList<Alert> Pending)> LoadedRulesAsync(
        ServiceEndpoint endpoint, CancellationToken ct)
    {
        var response = await proxy.GetAsync(endpoint, "api/v1/rules?type=alert", ct).ConfigureAwait(false);
        if (!response.Ok || Data(response.Json) is not { } data
            || !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return ([], []);

        var rules = new List<AlertRule>();
        var pending = new List<Alert>();

        foreach (var group in groups.EnumerateArray())
        {
            var groupName = Text(group, "name") ?? string.Empty;
            if (!group.TryGetProperty("rules", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var rule in items.EnumerateArray())
            {
                // Recording rules share the endpoint and are not alerts; ?type=alert should have
                // dropped them, but an older Prometheus ignores the filter.
                if (Text(rule, "type") is { } type && !string.Equals(type, "alerting", StringComparison.Ordinal))
                    continue;

                if (Text(rule, "name") is not { Length: > 0 } name)
                    continue;

                rules.Add(new AlertRule
                {
                    Name = name,
                    Expr = Text(rule, "query") ?? string.Empty,
                    Group = groupName,
                    // Prometheus reports the rule file, not the PrometheusRule it was rendered from.
                    // Reversing the operator's file-naming scheme would be a guess, and a wrong
                    // namespace on a jump link is worse than no link.
                    Namespace = null,
                    For = Duration(rule),
                    Labels = Strings(rule, "labels") ?? new Dictionary<string, string>(),
                    Annotations = Strings(rule, "annotations") ?? new Dictionary<string, string>(),
                    State = StateOf(Text(rule, "state")),
                    Health = Text(rule, "health") ?? "unknown",
                    LastError = Text(rule, "lastError") is { Length: > 0 } e ? e : null,
                });

                pending.AddRange(PendingOf(rule));
            }
        }

        return (rules, pending);
    }

    /// <summary>The instances of one rule that are true but have not outlasted its <c>for</c> yet.</summary>
    private static IEnumerable<Alert> PendingOf(JsonElement rule)
    {
        if (!rule.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var alert in alerts.EnumerateArray())
        {
            if (StateOf(Text(alert, "state")) != AlertState.Pending)
                continue;

            if (Strings(alert, "labels") is not { Count: > 0 } labels)
                continue;

            yield return new Alert
            {
                Labels = labels,
                Annotations = Strings(alert, "annotations") ?? new Dictionary<string, string>(),
                State = AlertState.Pending,
                StartsAt = Time(alert, "activeAt") ?? DateTimeOffset.UtcNow,
            };
        }
    }

    public async ValueTask<IReadOnlyList<Silence>> ListSilencesAsync(CancellationToken ct = default)
    {
        if (alertmanager is null)
            return [];

        var response = await proxy.GetAsync(alertmanager, "api/v2/silences", ct).ConfigureAwait(false);
        if (!response.Ok || response.Json is not { ValueKind: JsonValueKind.Array } array)
            return [];

        var silences = new List<Silence>();
        foreach (var item in array.EnumerateArray())
        {
            if (Text(item, "id") is not { Length: > 0 } id)
                continue;

            silences.Add(new Silence
            {
                Id = id,
                Matchers = [.. Matchers(item)],
                StartsAt = Time(item, "startsAt") ?? DateTimeOffset.MinValue,
                EndsAt = Time(item, "endsAt") ?? DateTimeOffset.MaxValue,
                CreatedBy = Text(item, "createdBy") ?? string.Empty,
                Comment = Text(item, "comment") ?? string.Empty,
                Status = SilenceStatusOf(
                    item.TryGetProperty("status", out var s) ? Text(s, "state") : null),
            });
        }

        return silences;
    }

    private static IEnumerable<SilenceMatcher> Matchers(JsonElement silence)
    {
        if (!silence.TryGetProperty("matchers", out var matchers) || matchers.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var matcher in matchers.EnumerateArray())
            if (Text(matcher, "name") is { Length: > 0 } name)
                yield return new SilenceMatcher
                {
                    Name = name,
                    Value = Text(matcher, "value") ?? string.Empty,
                    IsRegex = Flag(matcher, "isRegex") ?? false,
                    // Absent means equality: Alertmanager only started sending isEqual in 0.22.
                    IsEqual = Flag(matcher, "isEqual") ?? true,
                };
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    public async ValueTask<string> CreateSilenceAsync(SilenceRequest request, CancellationToken ct = default)
    {
        var endpoint = alertmanager ?? throw new NotSupportedException(
            "This cluster has no reachable Alertmanager, so there is nothing to silence.");

        var body = JsonSerializer.Serialize(new
        {
            matchers = request.Matchers.Select(m => new
            {
                name = m.Name,
                value = m.Value,
                isRegex = m.IsRegex,
                isEqual = m.IsEqual,
            }),
            startsAt = request.StartsAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            endsAt = request.EndsAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            createdBy = request.CreatedBy,
            comment = request.Comment,
        });

        var response = await proxy.PostAsync(endpoint, "api/v2/silences", body, ct).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException($"Could not create the silence: {response.Describe()}.");

        return response.Json is { } json && Text(json, "silenceID") is { Length: > 0 } id
            ? id
            : throw new InvalidOperationException("Alertmanager accepted the silence but returned no id.");
    }

    public async ValueTask ExpireSilenceAsync(string id, CancellationToken ct = default)
    {
        var endpoint = alertmanager ?? throw new NotSupportedException(
            "This cluster has no reachable Alertmanager, so there is nothing to expire.");

        // Singular, unlike the collection it was created against — Alertmanager's own spelling.
        var response = await proxy.DeleteAsync(endpoint, $"api/v2/silence/{Uri.EscapeDataString(id)}", ct)
            .ConfigureAwait(false);

        if (!response.Ok)
            throw new InvalidOperationException($"Could not expire the silence: {response.Describe()}.");
    }

    // ── Checking an expression ───────────────────────────────────────────────

    public async ValueTask<ExprCheck> CheckExprAsync(string promql, CancellationToken ct = default)
    {
        if (prometheus is null)
            return new ExprCheck { Parsed = false, Error = "No Prometheus is reachable from this cluster." };

        var response = await proxy
            .GetAsync(prometheus, $"api/v1/query?query={Uri.EscapeDataString(promql)}", ct)
            .ConfigureAwait(false);

        if (response.Json is not { } root)
            return new ExprCheck { Parsed = false, Error = response.Describe() };

        // A rejected expression is a 400 with Prometheus' own message, which is the useful half.
        if (Text(root, "status") != "success")
            return new ExprCheck { Parsed = false, Error = Text(root, "error") ?? response.Describe() };

        return new ExprCheck { Parsed = true, Samples = [.. Samples(Data(root))] };
    }

    private static IEnumerable<ExprSample> Samples(JsonElement? data)
    {
        if (data is not { } d || !d.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var series in result.EnumerateArray())
        {
            // Instant vectors carry "value"; a scalar carries one too. A range query would carry
            // "values", which this never asks for.
            if (!series.TryGetProperty("value", out var pair)
                || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
                continue;

            // The value is a string so that NaN and Inf survive JSON.
            if (!double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            yield return new ExprSample(Strings(series, "metric") ?? new Dictionary<string, string>(), value);
        }
    }

    // ── Reading JSON without trusting it ─────────────────────────────────────

    private static JsonElement? Data(JsonElement? root) =>
        root is { } r && r.ValueKind == JsonValueKind.Object
        && r.TryGetProperty("data", out var data) ? data : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static Dictionary<string, string>? Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString()!;

        return result;
    }

    private static IReadOnlyList<string> TextArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return [.. array.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)];
    }

    /// <summary>Receivers arrive as objects with a name, not as bare strings.</summary>
    private static IReadOnlyList<string> ReceiverNames(JsonElement alert)
    {
        if (!alert.TryGetProperty("receivers", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return [.. array.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.String ? r.GetString() : Text(r, "name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)];
    }

    /// <summary><c>for</c>, which Prometheus reports as seconds.</summary>
    private static TimeSpan? Duration(JsonElement rule) =>
        rule.TryGetProperty("duration", out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static AlertState StateOf(string? state) => state switch
    {
        "firing" => AlertState.Firing,
        "pending" => AlertState.Pending,
        _ => AlertState.Inactive,
    };

    private static SilenceStatus SilenceStatusOf(string? state) => state switch
    {
        "pending" => SilenceStatus.Pending,
        "expired" => SilenceStatus.Expired,
        _ => SilenceStatus.Active,
    };
}
