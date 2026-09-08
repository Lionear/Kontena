using System.Text;

namespace Kontena.Sdk.Orchestration.Models;

/// <summary>
/// The one place that moves a ConfigMap's or Secret's value between the two forms it has: the
/// base64 the API stores, and the text a person edits (KON-422).
/// <para>
/// It lives on the SDK beside <see cref="ConfigEntry"/> because both sides of the write need the
/// same answer — the editor encoding what was typed, and an engine decoding what came back. Two
/// implementations of "is this text?" would eventually disagree, and the disagreement would show up
/// as a certificate rendered as characters.
/// </para>
/// </summary>
public static class ConfigBytes
{
    /// <summary>
    /// Strict on purpose. The lenient decoder answers every byte sequence by substituting U+FFFD,
    /// which would make a TLS key look like text made of question marks; throwing is what lets
    /// <see cref="ToEntry"/> say "these bytes are not text" instead.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The value as the API holds it, whichever form the entry arrived in.</summary>
    public static string Base64Of(ConfigEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Base64.Length > 0)
            return entry.Base64;

        return entry.Text is { } text ? Encode(text) : string.Empty;
    }

    public static string Encode(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));

    /// <summary>
    /// One entry from what the API holds. <see cref="ConfigEntry.Text"/> stays null when the bytes
    /// are not valid UTF-8, and the size is the decoded length rather than the base64 one — 1.7 kB
    /// of certificate is what the row has to say, not the 2.3 kB its encoding takes.
    /// </summary>
    public static ConfigEntry ToEntry(string key, string base64)
    {
        var bytes = Decode(base64);

        string? text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = null;
        }

        return new ConfigEntry
        {
            Key = key,
            Text = text,
            Base64 = base64 ?? string.Empty,
            SizeBytes = bytes.Length,
        };
    }

    /// <summary>
    /// Bytes from base64, or none when it is not base64 at all. A hand-edited manifest is a place
    /// where that happens, and the answer there is an entry of zero bytes rather than a crash on
    /// the way to showing the page.
    /// </summary>
    private static byte[] Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return [];

        Span<byte> buffer = new byte[((base64.Length * 3) / 4) + 3];
        return Convert.TryFromBase64String(base64, buffer, out var written)
            ? buffer[..written].ToArray()
            : [];
    }
}
