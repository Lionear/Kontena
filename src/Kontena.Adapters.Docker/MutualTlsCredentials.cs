using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Kontena.Sdk.Errors;

namespace Kontena.Adapters.Docker;

/// <summary>
/// Client-certificate credentials for a TLS Docker endpoint, with the server checked against the CA that
/// issued it (KON-46).
/// <para>
/// Written here rather than taken from <c>Docker.DotNet.X509</c> for one reason worth the twenty lines:
/// that package presents the client certificate but leaves <c>ca.pem</c> unused, so the server is trusted
/// on the machine's ordinary root store. A Docker daemon's certificate is almost always signed by a CA of
/// its own that no root store knows, so the practical outcomes are either "it fails" or "verification is
/// turned off" — and the second one accepts any server that answers on that address, which is exactly
/// what mTLS is supposed to prevent.
/// </para>
/// </summary>
internal sealed class MutualTlsCredentials : Credentials
{
    private readonly X509Certificate2 _client;
    private readonly X509Certificate2? _authority;

    private MutualTlsCredentials(X509Certificate2 client, X509Certificate2? authority)
    {
        _client = client;
        _authority = authority;
    }

    /// <summary>
    /// Loads the <c>DOCKER_CERT_PATH</c> layout — <c>ca.pem</c>, <c>cert.pem</c>, <c>key.pem</c> — so an
    /// existing Docker TLS setup can be pointed at rather than rebuilt.
    /// </summary>
    public static MutualTlsCredentials FromDirectory(string directory)
    {
        var certificate = Path.Combine(directory, "cert.pem");
        var key = Path.Combine(directory, "key.pem");

        if (!File.Exists(certificate) || !File.Exists(key))
        {
            throw new EngineException(
                $"cert.pem and key.pem were not both found in {directory}. Docker keeps ca.pem, cert.pem "
                + "and key.pem together — point Kontena at that directory.");
        }

        var pair = X509Certificate2.CreateFromPemFile(certificate, key);

        // On Windows a certificate with an ephemeral key cannot be used for TLS client authentication;
        // round-tripping through a PFX is the documented way to get a usable one.
        var client = OperatingSystem.IsWindows()
            ? X509CertificateLoader.LoadPkcs12(pair.Export(X509ContentType.Pkcs12), password: null)
            : pair;

        // CreateFromPem, not CreateFromPemFile: the single-argument file overload looks for the private
        // key in that same file and throws when it is not there. A ca.pem is a certificate and nothing
        // else, so every standard DOCKER_CERT_PATH directory took that throw — which is to say the CA
        // was never loaded and a TLS endpoint with one never connected (KON-368).
        var caPath = Path.Combine(directory, "ca.pem");
        var authority = File.Exists(caPath)
            ? X509Certificate2.CreateFromPem(File.ReadAllText(caPath))
            : null;

        return new MutualTlsCredentials(client, authority);
    }

    public override bool IsTlsCredentials() => true;

    public override HttpMessageHandler GetHandler(HttpMessageHandler innerHandler)
    {
        if (innerHandler is not SocketsHttpHandler handler)
            return innerHandler;

        handler.SslOptions.ClientCertificates = [_client];

        if (_authority is not null)
            handler.SslOptions.RemoteCertificateValidationCallback = ValidateAgainstAuthority;

        return innerHandler;
    }

    /// <summary>
    /// Accepts the server only when the chain ends at the CA from <c>ca.pem</c>. Everything else about the
    /// chain — expiry, signatures — is still checked; the CA is added as a trusted root for this
    /// connection only, rather than the check being skipped.
    /// <para>
    /// Every certificate goes through that, including one the platform was already happy with (KON-366).
    /// It used to return early on <c>SslPolicyErrors.None</c>, which means "this chains to a root in the
    /// machine store" — a different claim from the one <c>ca.pem</c> makes, and a weaker one: anyone who
    /// can get a certificate for that host from any public CA would have passed, which is the outcome
    /// someone naming their own CA is trying to rule out.
    /// </para>
    /// </summary>
    internal bool ValidateAgainstAuthority(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null || _authority is null)
            return false;

        // A name mismatch is not something to wave through: it is the one error that means "this is not the
        // host you asked for".
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var verification = new X509Chain
        {
            ChainPolicy =
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,

                // A CA minted for one Docker daemon publishes neither a CRL nor an OCSP responder, so
                // asking would fail every connection rather than catch anything. Revocation for this
                // shape of trust is removing ca.pem.
                RevocationMode = X509RevocationMode.NoCheck,
            },
        };
        verification.ChainPolicy.CustomTrustStore.Add(_authority);

        return verification.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
    }

    /// <summary>
    /// <c>Credentials.Dispose</c> is not virtual in this version, so the certificates are released by
    /// hiding it rather than overriding — the client disposes credentials through this type.
    /// </summary>
    public new void Dispose()
    {
        _client.Dispose();
        _authority?.Dispose();
        base.Dispose();
    }
}
