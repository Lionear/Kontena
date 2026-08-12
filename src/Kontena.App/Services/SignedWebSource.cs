using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Kontena.App.Services;

/// <summary>
/// The update feed, with a publisher signature in front of it.
/// <para>
/// Velopack checks every package it downloads — the full one and each delta — against the SHA256 in
/// <c>releases.&lt;channel&gt;.json</c>, so that one file is the whole trust anchor: whoever writes it
/// decides what gets unpacked over the installation and launched. Until now the only thing standing
/// behind it was GitHub's TLS certificate, which says the bytes came from github.com and nothing at
/// all about who put them there. Writing a release asset — a leaked token, an over-privileged
/// workflow, a compromised account — was therefore enough to run code on every install on stable,
/// preview or nightly, and the user would have seen an ordinary update card (KON-363).
/// </para>
/// <para>
/// So the feed is fetched together with a detached signature the Build workflow publishes next to it,
/// <c>releases.&lt;channel&gt;.json.sig</c>, and verified against the public key baked into this build
/// before a single asset name is read out of it. The private half exists only as a repository secret,
/// so writing an asset no longer decides anything.
/// </para>
/// <para>
/// This covers the update chain, which is the part that runs without anyone looking. A first install
/// — Setup.exe, the .dmg, the .AppImage — is still unsigned, and no key of ours can change that: the
/// platforms only trust their own authorities.
/// </para>
/// <para>
/// ponytail: covers updates only. Authenticode on Windows and Developer ID + notarization on macOS
/// (KON-53) each need a certificate from an authority, and neither exists yet; they belong in the
/// Build workflow's pack step, not here, and nothing in this class changes when they land.
/// </para>
/// </summary>
internal sealed class SignedWebSource(string baseUrl) : SimpleWebSource(baseUrl)
{
    /// <summary>
    /// What the user reads on the update card. It names the outcome rather than the cipher, because
    /// the one thing that matters to them is that nothing was installed and that the copy they have
    /// is untouched.
    /// </summary>
    private const string Rejected =
        "This update is not signed by Kontena's release key, so it was not installed and nothing on"
        + " this machine has changed. Check https://github.com/Lionear/Kontena/releases before"
        + " installing anything by hand.";

    private static readonly string PublicKeyPem = ReadPublicKey();

    public override async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var feedUrl = $"{BaseUri.ToString().TrimEnd('/')}/releases.{channel}.json";
        logger.Info($"Downloading signed release feed from '{feedUrl}'.");

        // Bytes rather than DownloadString: a signature is over exactly the bytes the server sent,
        // and a round trip through a string decoder is not required to hand those back.
        var feed = await Downloader.DownloadBytes(feedUrl, timeout: Timeout).ConfigureAwait(false);

        byte[] signature;
        try
        {
            signature = await Downloader.DownloadBytes(feedUrl + ".sig", timeout: Timeout).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // The feed itself was found, so this is not "no release published yet" — it is a release
            // with its signature taken away, which is exactly what stripping the check would look
            // like. Say that instead of letting the 404 read as a publish in progress.
            throw new CryptographicException(Rejected, e);
        }

        Verify(feed, signature, PublicKeyPem);
        return VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feed));
    }

    /// <summary>
    /// Throws unless <paramref name="signature"/> is the release key's signature over
    /// <paramref name="feed"/>.
    /// </summary>
    /// <param name="feed">The feed bytes as served.</param>
    /// <param name="signature">The detached signature published beside them.</param>
    /// <param name="publicKeyPem">The verifying key, in SubjectPublicKeyInfo PEM.</param>
    internal static void Verify(byte[] feed, byte[] signature, string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);

        // Rfc3279DerSequence is what `openssl dgst -sign` writes for an EC key, and openssl is what
        // signs in the workflow. The other format (IEEE P1363) would silently never verify.
        if (!key.VerifyData(feed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException(Rejected);
    }

    /// <summary>
    /// The same <c>release-signing.pub.pem</c> the Build workflow verifies its own signatures against,
    /// carried along in the binary. One file, so a rotated key cannot end up half-applied.
    /// </summary>
    internal static string ReadPublicKey()
    {
        using var stream = typeof(SignedWebSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"This build carries no release signing key ({ResourceName} is missing).");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const string ResourceName = "Kontena.App.ReleaseSigningKey.pem";
}
