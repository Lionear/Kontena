namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// One key of a ConfigMap or Secret, without its value (KON-249).
/// <para>
/// The size is the length of the value in bytes, which is worth knowing — a 40-byte key is a
/// password and a 3 kB one is a certificate — and gives away nothing.
/// </para>
/// </summary>
public readonly record struct ConfigKey(string Name, long SizeBytes);

/// <summary>
/// A ConfigMap as the grid sees it: which keys it holds, not what is in them.
/// <para>
/// The values are deliberately absent from this model even for ConfigMaps, where they are not
/// sensitive. One shape for both kinds means the Secret path cannot become the exception that
/// someone later "simplifies" back into carrying values.
/// </para>
/// </summary>
public sealed record ConfigMapSummary
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    public IReadOnlyList<ConfigKey> Keys { get; init; } = [];

    public TimeSpan Age { get; init; }
}

/// <summary>
/// A Secret as the grid sees it — keys and sizes, never values.
/// <para>
/// Listing secrets does transfer their values over the wire: that is how the Kubernetes list API
/// works and no client can avoid it. What this model changes is what happens next — the adapter
/// keeps the keys and drops the values, so nothing downstream of the call can render, log or
/// serialise a secret by accident. A value is fetched again, by name, at the moment someone asks
/// to see it.
/// </para>
/// </summary>
public sealed record SecretSummary
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>The Secret's type, e.g. "Opaque", "kubernetes.io/tls", "kubernetes.io/dockerconfigjson".</summary>
    public string Type { get; init; } = "Opaque";

    public IReadOnlyList<ConfigKey> Keys { get; init; } = [];

    /// <summary>
    /// The Secret's labels, which are how a controller says it owns this object (KON-422).
    /// <para>
    /// Carried on the listing rather than fetched per row: whether a secret can be edited has to be
    /// answerable without a second call, and it is the same one call that already returns everything
    /// else the page shows.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public TimeSpan Age { get; init; }
}

/// <summary>
/// Whether something other than a person keeps a Secret's contents up to date (KON-422).
/// <para>
/// Editing such a Secret by hand is not forbidden — the apiserver takes the write — it is simply
/// pointless: the controller reconciles it back, on its own schedule, and the person who typed the
/// new value is left believing it took. So the editor asks this before offering itself.
/// </para>
/// </summary>
public static class ManagedSecrets
{
    /// <summary>
    /// The label the External Secrets Operator puts on <b>every</b> Secret it manages, whatever the
    /// ExternalSecret's <c>creationPolicy</c> is.
    /// <para>
    /// This, rather than the ownerReference: ESO only sets an owner under <c>creationPolicy: Owner</c>
    /// — the default, but <c>Orphan</c>, <c>Merge</c> and <c>CreateOrMerge</c> all leave the Secret
    /// unowned while ESO goes on rewriting its keys. The narrower signal would have called exactly
    /// the reconciled-but-unowned Secrets editable.
    /// </para>
    /// <para>
    /// Named <c>LabelManaged</c> in ESO's own API package, with the comment "all secrets managed by
    /// an ExternalSecret will have this label equal to true":
    /// <c>apis/externalsecrets/v1/externalsecret_types.go</c>.
    /// </para>
    /// </summary>
    public const string ExternalSecretsLabel = "reconcile.external-secrets.io/managed";

    /// <summary>The only value ESO writes for it — <c>LabelManagedValue</c>.</summary>
    public const string ExternalSecretsLabelValue = "true";

    /// <summary>
    /// Whether the External Secrets Operator reconciles this Secret.
    /// <para>
    /// The value is checked, not just the key: a label someone set to "false" by hand is a statement
    /// that it is not managed, and reading any value as "yes" would make the label impossible to
    /// turn off.
    /// </para>
    /// </summary>
    public static bool IsExternallyManaged(IReadOnlyDictionary<string, string>? labels) =>
        labels is not null
        && labels.TryGetValue(ExternalSecretsLabel, out var value)
        && string.Equals(value, ExternalSecretsLabelValue, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One key of a ConfigMap or Secret <b>with</b> its value — what a fetch returns.
/// <para>
/// A Secret's value is base64 in the API, and decoding it is not a disclosure: base64 is transport,
/// not protection, and a reader who is shown the encoded form is simply being asked to decode it
/// themselves. <see cref="Text"/> is therefore the decoded value — and null when the bytes are not
/// valid UTF-8 text, because a TLS key rendered as characters is noise that can also break a
/// terminal.
/// </para>
/// </summary>
public sealed record ConfigEntry
{
    public required string Key { get; init; }

    /// <summary>The decoded value, or null when it is not text.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// The value as base64 — always present, including for text.
    /// <para>
    /// This is what a binary value can be carried out as: it is the form the cluster stores it in,
    /// the form every other tool takes it back in, and the only one that survives a clipboard whole.
    /// Without it "copy" would have nothing to offer for exactly the keys — certificates, keystores —
    /// where copying is the only thing you can do.
    /// </para>
    /// </summary>
    public string Base64 { get; init; } = string.Empty;

    /// <summary>Length of the value in bytes, text or not.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Whether the value is bytes that are not text — a certificate, a keystore, an archive.</summary>
    public bool IsBinary => Text is null;
}
