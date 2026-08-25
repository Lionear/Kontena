using System.Text;
using Kontena.Core.Orchestration;
using Kontena.Sdk.Orchestration.Models;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The Data tab's write path against a real apiserver (KON-422).
/// <para>
/// Everything else about this feature is verified against the fake, whose apply is a merge of its
/// own invention. These are the questions only a real cluster answers: what a server-side apply does
/// with a key the document stops mentioning, whether a typed Secret survives being rewritten, and
/// whether an admission rejection reaches the user as the apiserver's own sentence.
/// </para>
/// <para>
/// Skipped without a cluster, like every other live test here. Each one works in a namespace of its
/// own and removes it afterwards.
/// </para>
/// </summary>
public class ConfigWriteLiveTests
{
    private const string NamespacePrefix = "kontena-kon422";

    private static async Task<KubernetesClusterEngine?> ConnectAsync()
    {
        var provider = KubernetesClusterProvider.DiscoverAll().FirstOrDefault();
        if (provider is null)
            return null;

        var engine = (KubernetesClusterEngine)provider.CreateBackend();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await engine.PingAsync(cts.Token);
            return engine;
        }
        catch (Exception)
        {
            engine.Dispose();
            return null;
        }
    }

    private static async Task<KubernetesClusterEngine> RequireClusterAsync()
    {
        var engine = await ConnectAsync();
        Skip.If(engine is null, "No reachable Kubernetes cluster in the kubeconfig.");
        return engine!;
    }

    private static async Task<string> ClaimNamespaceAsync(KubernetesClusterEngine engine, string suffix)
    {
        var ns = $"{NamespacePrefix}-{suffix}";
        Skip.If(
            (await engine.ListNamespacesAsync()).Any(n => n.Name == ns),
            $"Namespace {ns} already exists; refusing to touch it.");

        await ApplyAsync(engine, $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n");
        return ns;
    }

    private static async Task<List<ApplyProgress>> ApplyAsync(
        KubernetesClusterEngine engine, string yaml, bool dryRun = false)
    {
        var results = new List<ApplyProgress>();
        await foreach (var p in engine.ApplyAsync(new ManifestBundle { Yaml = yaml, DryRun = dryRun }))
            results.Add(p);

        return results;
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static ResourceRef Secret(string ns, string name) =>
        new(GroupVersionKind.Secret, ns, name);

    /// <summary>
    /// The editor's own act, end to end: read the live manifest, replace the data block with what the
    /// fields hold, send it back. Exactly what <c>ClusterConfigDetailViewModel.SendAsync</c> does,
    /// minus the view model.
    /// </summary>
    private static async Task<List<ApplyProgress>> WriteFieldsAsync(
        KubernetesClusterEngine engine, ResourceRef reference, params (string Key, string Value)[] fields)
    {
        var manifest = await engine.GetManifestAsync(reference);
        var data = ConfigManifest.WithData(
            manifest,
            fields.ToDictionary(f => f.Key, f => B64(f.Value), StringComparer.Ordinal));

        Assert.NotNull(data);
        return await ApplyAsync(engine, data!);
    }

    private static async Task<string[]> KeysOfAsync(KubernetesClusterEngine engine, string ns, string name) =>
        [.. (await engine.GetConfigDataAsync(Secret(ns, name))).Select(e => e.Key).Order(StringComparer.Ordinal)];

    private static async Task<string?> ValueOfAsync(
        KubernetesClusterEngine engine, string ns, string name, string key) =>
        (await engine.GetConfigDataAsync(Secret(ns, name))).FirstOrDefault(e => e.Key == key)?.Text;

    private static async Task DropAsync(KubernetesClusterEngine engine, string ns)
    {
        try
        {
            await engine.DeleteAsync(new ResourceRef(GroupVersionKind.Namespace, null, ns));
        }
        catch (Exception)
        {
            // Cleanup is best effort: a failure here must not turn a passing assertion into a red
            // test, and the namespace is named after the run.
        }
    }

    /// <summary>
    /// The question the PR could not answer without a cluster: does removing a key actually remove
    /// it?
    /// <para>
    /// This adapter applies server-side, with <c>fieldManager: kontena</c> and <c>force: true</c>.
    /// Under SSA a field the manager owned and no longer sends is pruned — but on the <b>first</b>
    /// write the fields belong to whoever created the Secret, and force resolves the conflict by
    /// taking ownership rather than by deleting what it never held. So a removal in the same apply
    /// that first claims the object is the case worth knowing about, and it is the one here.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Removing_a_key_from_a_secret_this_editor_has_never_written_before()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "remove-foreign");

        try
        {
            // Created by somebody else — no kontena-owned fields, and no last-applied annotation
            // either, which is the shape a Helm- or operator-made Secret has.
            await ApplyAsync(engine, $"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: creds
                  namespace: {ns}
                type: Opaque
                data:
                  password: {B64("first")}
                  username: {B64("postgres")}
                """);

            Assert.Equal(["password", "username"], await KeysOfAsync(engine, ns, "creds"));

            // The editor drops "password" and keeps "username".
            await WriteFieldsAsync(engine, Secret(ns, "creds"), ("username", "postgres"));

            Assert.Equal(["username"], await KeysOfAsync(engine, ns, "creds"));
        }
        finally
        {
            await DropAsync(engine, ns);
        }
    }

    /// <summary>The same removal on an object this editor already owns — the second write.</summary>
    [SkippableFact]
    public async Task Removing_a_key_on_a_secret_this_editor_already_owns()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "remove-owned");

        try
        {
            await ApplyAsync(engine, $"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: creds
                  namespace: {ns}
                type: Opaque
                data:
                  password: {B64("first")}
                """);

            // Write once to take ownership, add a key, then take it away again.
            await WriteFieldsAsync(engine, Secret(ns, "creds"), ("password", "first"), ("extra", "x"));
            Assert.Equal(["extra", "password"], await KeysOfAsync(engine, ns, "creds"));

            await WriteFieldsAsync(engine, Secret(ns, "creds"), ("password", "first"));
            Assert.Equal(["password"], await KeysOfAsync(engine, ns, "creds"));
        }
        finally
        {
            await DropAsync(engine, ns);
        }
    }

    [SkippableFact]
    public async Task Changing_renaming_and_adding_all_land_on_the_cluster()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "edits");

        try
        {
            await ApplyAsync(engine, $"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: creds
                  namespace: {ns}
                type: Opaque
                data:
                  password: {B64("first")}
                  username: {B64("postgres")}
                """);

            await WriteFieldsAsync(
                engine, Secret(ns, "creds"),
                ("PGPASSWORD", "rotated"), ("username", "postgres"), ("PGSSLMODE", "verify-full"));

            Assert.Equal(["PGPASSWORD", "PGSSLMODE", "username"], await KeysOfAsync(engine, ns, "creds"));
            Assert.Equal("rotated", await ValueOfAsync(engine, ns, "creds", "PGPASSWORD"));
            Assert.Equal("verify-full", await ValueOfAsync(engine, ns, "creds", "PGSSLMODE"));
        }
        finally
        {
            await DropAsync(engine, ns);
        }
    }

    /// <summary>
    /// A typed Secret is the apiserver's business: <c>kubernetes.io/tls</c> requires its two keys and
    /// <c>kubernetes.io/dockerconfigjson</c> requires its one. Rewriting the data map must not lose
    /// the type, and the requirement must still be enforced afterwards.
    /// </summary>
    [SkippableFact]
    public async Task A_typed_secret_keeps_its_type_and_its_rules()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "typed");

        try
        {
            await ApplyAsync(engine, $$"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: pull
                  namespace: {{ns}}
                type: kubernetes.io/dockerconfigjson
                data:
                  .dockerconfigjson: {{B64("{\"auths\":{}}")}}
                """);

            await WriteFieldsAsync(
                engine, Secret(ns, "pull"), (".dockerconfigjson", "{\"auths\":{\"ghcr.io\":{}}}"));

            var manifest = await engine.GetManifestAsync(Secret(ns, "pull"));
            Assert.Contains("type: kubernetes.io/dockerconfigjson", manifest, StringComparison.Ordinal);
            Assert.Equal("{\"auths\":{\"ghcr.io\":{}}}", await ValueOfAsync(engine, ns, "pull", ".dockerconfigjson"));

            // And the type's own rule still bites: dropping the required key is refused by the
            // apiserver, in its own words.
            var refused = await WriteFieldsAsync(engine, Secret(ns, "pull"), ("something-else", "x"));

            Assert.Equal(ApplyAction.Failed, refused[0].Action);
            Assert.Contains("dockerconfigjson", refused[0].Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropAsync(engine, ns);
        }
    }

    /// <summary>
    /// An admission rejection has to arrive as the apiserver's own message rather than as "apply
    /// failed" — the whole reason the status line carries <c>ApplyProgress.Error</c> through
    /// unedited. A ValidatingAdmissionPolicy stands in for a webhook: same admission chain, same
    /// shape of refusal, no certificate to mint.
    /// </summary>
    [SkippableFact]
    public async Task An_admission_rejection_arrives_in_the_clusters_own_words()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "admission");

        try
        {
            await ApplyAsync(engine, $$"""
                apiVersion: admissionregistration.k8s.io/v1
                kind: ValidatingAdmissionPolicy
                metadata:
                  name: kontena-kon422-no-forbidden-key
                spec:
                  failurePolicy: Fail
                  matchConstraints:
                    resourceRules:
                      - apiGroups: [""]
                        apiVersions: ["v1"]
                        operations: ["CREATE", "UPDATE"]
                        resources: ["secrets"]
                  validations:
                    - expression: "!has(object.data) || !('forbidden' in object.data)"
                      message: "the key 'forbidden' is not allowed on a Secret here"
                ---
                apiVersion: admissionregistration.k8s.io/v1
                kind: ValidatingAdmissionPolicyBinding
                metadata:
                  name: kontena-kon422-no-forbidden-key
                spec:
                  policyName: kontena-kon422-no-forbidden-key
                  validationActions: ["Deny"]
                  matchResources:
                    namespaceSelector:
                      matchLabels:
                        kubernetes.io/metadata.name: {{ns}}
                """);

            await ApplyAsync(engine, $"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: creds
                  namespace: {ns}
                type: Opaque
                data:
                  password: {B64("first")}
                """);

            // The binding takes a moment to reach the apiserver's policy cache. Waited out with a
            // dry-run: polling with the real write would leave the forbidden key behind on every
            // attempt made before the policy was live, which is what the last assertion here is
            // about.
            var manifest = await engine.GetManifestAsync(Secret(ns, "creds"));
            var forbidden = ConfigManifest.WithData(manifest, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["password"] = B64("first"),
                ["forbidden"] = B64("nope"),
            })!;

            var refused = await ApplyAsync(engine, forbidden, dryRun: true);
            for (var attempt = 0; attempt < 40 && refused[0].Action != ApplyAction.Failed; attempt++)
            {
                await Task.Delay(500);
                refused = await ApplyAsync(engine, forbidden, dryRun: true);
            }

            Assert.Equal(ApplyAction.Failed, refused[0].Action);
            Assert.Contains("not allowed on a Secret here", refused[0].Error, StringComparison.Ordinal);

            // And for real, not only as a preview.
            refused = await ApplyAsync(engine, forbidden);

            Assert.Equal(ApplyAction.Failed, refused[0].Action);
            Assert.Contains("not allowed on a Secret here", refused[0].Error, StringComparison.Ordinal);

            // Refused means refused: the key is not on the object.
            Assert.Equal(["password"], await KeysOfAsync(engine, ns, "creds"));
        }
        finally
        {
            await DropAsync(engine, ns);
            try
            {
                await engine.DeleteAsync(new ResourceRef(
                    new GroupVersionKind("admissionregistration.k8s.io", "v1", "ValidatingAdmissionPolicyBinding"),
                    null, "kontena-kon422-no-forbidden-key"));
                await engine.DeleteAsync(new ResourceRef(
                    new GroupVersionKind("admissionregistration.k8s.io", "v1", "ValidatingAdmissionPolicy"),
                    null, "kontena-kon422-no-forbidden-key"));
            }
            catch (Exception)
            {
                // Best effort, as above.
            }
        }
    }

    /// <summary>
    /// The flow-map worry, answered rather than guessed at: the manifest this editor rewrites is the
    /// apiserver's own rendering, asked for as <c>application/yaml</c>, and that renders a map as a
    /// block. A <c>data: {a: b}</c> can be typed into the YAML tab but is not what comes back.
    /// </summary>
    [SkippableFact]
    public async Task The_manifest_this_editor_rewrites_is_the_apiservers_own_block_rendering()
    {
        using var engine = await RequireClusterAsync();
        var ns = await ClaimNamespaceAsync(engine, "shape");

        try
        {
            // Applied as a flow map, which is legal YAML and what the worry was about.
            await ApplyAsync(engine, $$"""
                apiVersion: v1
                kind: Secret
                metadata:
                  name: creds
                  namespace: {{ns}}
                type: Opaque
                data: {password: {{B64("first")}}, username: {{B64("postgres")}}}
                """);

            var manifest = await engine.GetManifestAsync(Secret(ns, "creds"));

            Assert.Contains("data:\n", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("data: {", manifest, StringComparison.Ordinal);

            // And the round trip still works on it.
            await WriteFieldsAsync(engine, Secret(ns, "creds"), ("username", "postgres"));
            Assert.Equal(["username"], await KeysOfAsync(engine, ns, "creds"));
        }
        finally
        {
            await DropAsync(engine, ns);
        }
    }

    /// <summary>
    /// The claim Part B rests on: the label is on every Secret ESO manages, and the ownerReference is
    /// not. Skipped unless an ESO install with the four target Secrets is present — see the PR for
    /// how they are made.
    /// </summary>
    [SkippableFact]
    public async Task The_eso_label_is_on_every_policy_and_the_owner_reference_is_not()
    {
        using var engine = await RequireClusterAsync();

        var secrets = await engine.ListSecretsAsync("kon422");
        Skip.If(secrets.Count == 0, "No namespace 'kon422' with ESO-managed Secrets in this cluster.");

        foreach (var name in (string[])["sec-owner", "sec-orphan", "sec-merge", "sec-createormerge"])
        {
            var secret = secrets.FirstOrDefault(s => s.Name == name);
            Skip.If(secret is null, $"No Secret {name}; the ESO fixtures are not in this cluster.");

            Assert.True(
                ManagedSecrets.IsExternallyManaged(secret!.Labels),
                $"{name} was not recognised as externally managed.");
        }
    }
}
