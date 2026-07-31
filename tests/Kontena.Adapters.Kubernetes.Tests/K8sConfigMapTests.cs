using System.Text;
using k8s.Models;
using Kontena.Adapters.Kubernetes;

namespace Kontena.Adapters.Kubernetes.Tests;

/// <summary>
/// The ConfigMap and Secret mappers (KON-249). Two things are worth pinning here and both are about
/// what does <b>not</b> come out: the summaries carry no values, and a value that is not text is
/// never presented as text.
/// </summary>
public class K8sConfigMapTests
{
    private static V1Secret Secret(IDictionary<string, byte[]>? data = null, string type = "Opaque") => new()
    {
        Metadata = new V1ObjectMeta { Name = "postgres-credentials", NamespaceProperty = "app" },
        Type = type,
        Data = data ?? new Dictionary<string, byte[]>
        {
            ["password"] = Encoding.UTF8.GetBytes("s3cr3t"),
            ["username"] = Encoding.UTF8.GetBytes("postgres"),
        },
    };

    [Fact]
    public void A_secret_summary_keeps_the_keys_and_drops_the_values()
    {
        // This is the seam where the values the list API sent stop. Everything downstream of it —
        // grids, rows, logs, a crash dump — is then unable to show a secret it was never asked for.
        var mapped = K8sMap.ToSecret(Secret());

        Assert.Equal(["password", "username"], mapped.Keys.Select(k => k.Name));
        Assert.Equal(6, mapped.Keys.Single(k => k.Name == "password").SizeBytes);
        Assert.Equal("Opaque", mapped.Type);

        // There is nowhere on the summary for a value to be, and that is the assertion: the type has
        // no member that could hold one.
        Assert.DoesNotContain(
            typeof(Sdk.Orchestration.Models.SecretSummary).GetProperties(),
            p => p.Name is "Data" or "Values" or "Text");
    }

    [Fact]
    public void A_secret_with_no_type_is_Opaque_rather_than_blank()
    {
        // The API omits the field for a plain secret; a blank column would read as unknown.
        Assert.Equal("Opaque", K8sMap.ToSecret(Secret(type: string.Empty)).Type);
    }

    [Fact]
    public void Keys_come_out_in_a_stable_order()
    {
        // The API hands back a map, whose order is not an order. Without this the rows shuffle
        // between refreshes for no reason a reader can see.
        var mapped = K8sMap.ToSecret(Secret(new Dictionary<string, byte[]>
        {
            ["zeta"] = [1], ["alpha"] = [1], ["mu"] = [1],
        }));

        Assert.Equal(["alpha", "mu", "zeta"], mapped.Keys.Select(k => k.Name));
    }

    [Fact]
    public void A_text_value_is_decoded_and_also_offered_as_base64()
    {
        // Decoding base64 is not a disclosure: it is transport, not protection, and showing the
        // encoded form only asks the reader to decode it themselves.
        var entry = K8sMap.ToEntries(Secret()).Single(e => e.Key == "password");

        Assert.Equal("s3cr3t", entry.Text);
        Assert.False(entry.IsBinary);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("s3cr3t")), entry.Base64);
    }

    [Fact]
    public void Bytes_that_are_not_valid_utf8_come_back_as_binary_rather_than_as_mangled_text()
    {
        // 0xFF is not a UTF-8 sequence. A lossy decode would put a replacement character on screen
        // and claim it was in the secret.
        var entry = K8sMap
            .ToEntries(Secret(new Dictionary<string, byte[]> { ["tls.key"] = [0xFF, 0xFE, 0x00, 0x01] }))
            .Single();

        Assert.True(entry.IsBinary);
        Assert.Null(entry.Text);
        Assert.Equal(4, entry.SizeBytes);

        // And it is still carryable: base64 is the only form that survives a clipboard whole.
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x00, 0x01 }), entry.Base64);
    }

    [Fact]
    public void A_config_map_counts_text_in_bytes_rather_than_in_characters()
    {
        // "é" is one character and two bytes. The size column sits next to a byte count everywhere
        // else in the app, and a character count would quietly disagree with it.
        var mapped = K8sMap.ToConfigMap(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "web-config", NamespaceProperty = "app" },
            Data = new Dictionary<string, string> { ["motd"] = "café" },
        });

        Assert.Equal(5, mapped.Keys.Single().SizeBytes);
    }

    [Fact]
    public void A_config_maps_binary_half_is_listed_alongside_its_text_half()
    {
        // binaryData is a separate field, and a mapper that read only data would silently show an
        // object as having fewer keys than it has.
        var mapped = K8sMap.ToConfigMap(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "web-config", NamespaceProperty = "app" },
            Data = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" },
            BinaryData = new Dictionary<string, byte[]> { ["favicon.ico"] = [0x00, 0x01, 0x02] },
        });

        // Ordinal, so uppercase sorts first — deterministic is what matters, and a culture-aware
        // sort would order the same object differently on two machines.
        Assert.Equal(["LOG_LEVEL", "favicon.ico"], mapped.Keys.Select(k => k.Name));

        var entries = K8sMap.ToEntries(new V1ConfigMap
        {
            Data = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" },
            BinaryData = new Dictionary<string, byte[]> { ["favicon.ico"] = [0x00, 0x01, 0x02] },
        });

        Assert.True(entries.Single(e => e.Key == "favicon.ico").IsBinary);
        Assert.Equal("info", entries.Single(e => e.Key == "LOG_LEVEL").Text);
    }
}
