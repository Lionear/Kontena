using Kontena.Sdk.Orchestration.Models;
using Xunit;

namespace Kontena.Core.Orchestration.Tests;

/// <summary>
/// Putting edited keys back into a manifest without re-serialising the rest of it (KON-422).
/// <para>
/// The whole reason this is text surgery is that everything the editor never saw has to survive:
/// annotations a controller wrote, an ownerReference, a type. Most of what is asserted below is
/// therefore about the lines that were <em>not</em> touched.
/// </para>
/// </summary>
public sealed class ConfigManifestTests
{
    private const string Secret = """
        apiVersion: v1
        kind: Secret
        metadata:
          name: postgres-credentials
          namespace: app
          annotations:
            kubectl.kubernetes.io/last-applied-configuration: '{"apiVersion":"v1"}'
          ownerReferences:
            - apiVersion: external-secrets.io/v1
              kind: ExternalSecret
              name: postgres
        type: Opaque
        data:
          password: czNjcjN0
          username: cG9zdGdyZXM=
        """;

    private static IReadOnlyDictionary<string, string> Data(params (string Key, string Base64)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Base64, StringComparer.Ordinal);

    [Fact]
    public void The_data_block_is_replaced_and_nothing_else_is()
    {
        var result = ConfigManifest.WithData(Secret, Data(("password", "cm90YXRlZA=="), ("username", "cG9zdGdyZXM=")));

        Assert.NotNull(result);
        Assert.Contains("  password: cm90YXRlZA==", result, StringComparison.Ordinal);
        Assert.DoesNotContain("czNjcjN0", result, StringComparison.Ordinal);

        // Everything the editor never showed, still there.
        Assert.Contains("type: Opaque", result, StringComparison.Ordinal);
        Assert.Contains("kind: ExternalSecret", result, StringComparison.Ordinal);
        Assert.Contains("last-applied-configuration", result, StringComparison.Ordinal);
        Assert.Contains("namespace: app", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_key_is_absent_rather_than_emptied()
    {
        var result = ConfigManifest.WithData(Secret, Data(("username", "cG9zdGdyZXM=")));

        Assert.DoesNotContain("password", result, StringComparison.Ordinal);
        Assert.Contains("  username: cG9zdGdyZXM=", result, StringComparison.Ordinal);
    }

    [Fact]
    public void An_added_key_lands_in_the_block()
    {
        var result = ConfigManifest.WithData(
            Secret, Data(("password", "czNjcjN0"), ("username", "cG9zdGdyZXM="), ("PGSSLMODE", "dmVyaWZ5LWZ1bGw=")));

        // Sorted, so a re-read of what was written does not look like a change.
        var lines = result!.Split('\n').SkipWhile(l => l != "data:").Skip(1).Take(3).ToList();
        Assert.Equal(["  PGSSLMODE: dmVyaWZ5LWZ1bGw=", "  password: czNjcjN0", "  username: cG9zdGdyZXM="], lines);
    }

    /// <summary>
    /// stringData is the apiserver's convenience half: it wins over data for the same key. Leaving
    /// one behind would mean an edited value that never takes, which is this ticket's whole bug in a
    /// different disguise.
    /// </summary>
    [Fact]
    public void A_stringData_block_is_removed_rather_than_left_to_win()
    {
        const string withStringData = """
            apiVersion: v1
            kind: Secret
            metadata:
              name: s
            stringData:
              password: in-the-clear
            data:
              username: cG9zdGdyZXM=
            """;

        var result = ConfigManifest.WithData(withStringData, Data(("password", "cm90YXRlZA==")));

        Assert.DoesNotContain("stringData", result, StringComparison.Ordinal);
        Assert.DoesNotContain("in-the-clear", result, StringComparison.Ordinal);
        Assert.Contains("  password: cm90YXRlZA==", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_with_no_data_block_gets_one()
    {
        const string empty = """
            apiVersion: v1
            kind: Secret
            metadata:
              name: s
            type: Opaque
            """;

        var result = ConfigManifest.WithData(empty, Data(("first", "Zmly")));

        Assert.Contains("type: Opaque", result, StringComparison.Ordinal);
        Assert.EndsWith("data:\n  first: Zmly\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_block_that_follows_data_is_not_swallowed()
    {
        const string trailing = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: c
            data:
              a: YQ==
            binaryData:
              b: Yg==
            """;

        var result = ConfigManifest.WithData(trailing, Data(("a", "QQ==")));

        Assert.Contains("binaryData:", result, StringComparison.Ordinal);
        Assert.Contains("  b: Yg==", result, StringComparison.Ordinal);
        Assert.Contains("  a: QQ==", result, StringComparison.Ordinal);
    }

    /// <summary>Refusing beats guessing which document in a bundle was meant.</summary>
    [Fact]
    public void A_bundle_of_documents_is_refused()
    {
        const string bundle = """
            apiVersion: v1
            kind: Secret
            metadata:
              name: a
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: b
            """;

        Assert.Null(ConfigManifest.WithData(bundle, Data(("x", "eA=="))));
    }

    /// <summary>A single document may still announce itself with a leading marker.</summary>
    [Fact]
    public void A_leading_document_marker_is_not_a_bundle()
    {
        const string marked = """
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: a
            data:
              x: eA==
            """;

        Assert.Contains("  x: eQ==", ConfigManifest.WithData(marked, Data(("x", "eQ=="))), StringComparison.Ordinal);
    }

    /// <summary>What a failed fetch produces is a comment, and a comment is not an object.</summary>
    [Fact]
    public void Something_that_is_not_a_manifest_is_refused()
    {
        Assert.Null(ConfigManifest.WithData("# Secret/app/x was not found in this cluster.", Data(("x", "eA=="))));
        Assert.Null(ConfigManifest.WithData("   ", Data(("x", "eA=="))));
    }

    [Fact]
    public void Rows_become_data_with_text_encoded_and_bytes_passed_through()
    {
        var data = ConfigManifest.DataOf(
        [
            new ConfigEntry { Key = "username", Text = "postgres" },
            new ConfigEntry { Key = "tls.crt", Text = null, Base64 = "3q2+7w==" },
        ]);

        Assert.Equal("cG9zdGdyZXM=", data["username"]);
        Assert.Equal("3q2+7w==", data["tls.crt"]);
    }
}
