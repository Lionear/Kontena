using System.Security.Cryptography;
using System.Text;
using Kontena.App.Services;
using Xunit;

namespace Kontena.App.Tests;

/// <summary>
/// The check that decides whether a downloaded update is allowed to replace this installation
/// (KON-363). Everything downstream — the packages, the deltas — hangs off the SHA256s in the feed
/// these signatures cover, so a hole here is a hole in the whole update chain.
/// </summary>
public sealed class SignedWebSourceTests
{
    private static readonly byte[] Feed = Encoding.UTF8.GetBytes(
        """{"Assets":[{"PackageId":"Kontena","Version":"0.4.0","Type":"Full","FileName":"Kontena-0.4.0-linux-stable-full.nupkg","SHA256":"F39F28","Size":3078485}]}""");

    /// <summary>
    /// Signed the way the Build workflow signs — <c>openssl dgst -sha256 -sign</c> over an EC key,
    /// which writes the DER sequence rather than the raw pair.
    /// </summary>
    private static byte[] Sign(ECDsa key, byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    [Fact]
    public void The_release_key_signature_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        SignedWebSource.Verify(Feed, Sign(key, Feed), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>
    /// The attack the ticket describes: someone who can write a release asset rewrites the feed to
    /// point at their own package. They cannot re-sign it, so the old signature no longer fits.
    /// </summary>
    [Fact]
    public void A_feed_edited_after_signing_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Sign(key, Feed);

        var tampered = (byte[])Feed.Clone();
        tampered[^2] ^= 0x01;

        Assert.Throws<CryptographicException>(
            () => SignedWebSource.Verify(tampered, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    /// <summary>The same attack with a whole feed of their own, signed by a key of their own.</summary>
    [Fact]
    public void Another_key_is_refused()
    {
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ours = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<CryptographicException>(
            () => SignedWebSource.Verify(Feed, Sign(theirs, Feed), ours.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void An_empty_signature_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<CryptographicException>(
            () => SignedWebSource.Verify(Feed, [], key.ExportSubjectPublicKeyInfoPem()));
    }

    /// <summary>
    /// The key ships as an embedded resource under a logical name spelled out in the csproj, and the
    /// only thing that reads it is an update check on a user's machine. A rename or a mangled PEM
    /// would surface there, months later, as "updates stopped working" — so it is asserted here.
    /// </summary>
    [Fact]
    public void This_build_carries_a_usable_release_key()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(SignedWebSource.ReadPublicKey());

        Assert.Equal(256, key.KeySize);
    }
}
