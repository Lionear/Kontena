using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kontena.Adapters.Docker;

namespace Kontena.Adapters.Docker.Tests;

/// <summary>
/// The pin a <c>ca.pem</c> is (KON-366).
/// <para>
/// The type exists because <c>Docker.DotNet.X509</c> presents the client certificate and then trusts
/// the server on the machine's ordinary root store. Pointing Kontena at a CA has to mean <em>that</em>
/// CA, which is what these tests hold it to — the certificates are minted here rather than committed,
/// so nothing in the repo expires.
/// </para>
/// </summary>
public class MutualTlsCredentialsTests
{
    [Fact]
    public void A_server_from_another_authority_is_refused_even_when_the_platform_is_content()
    {
        using var pinned = Authority("CN=Kontena Test CA");
        using var stranger = Authority("CN=Some Public CA");
        using var server = LeafSignedBy(stranger, "docker.test");

        using var credentials = CredentialsPinnedTo(pinned);

        // SslPolicyErrors.None is what the platform reports for a chain it already trusts. That used to
        // return true before the chain was ever built against ca.pem — so a certificate from any CA the
        // machine happens to trust came straight through.
        Assert.False(credentials.ValidateAgainstAuthority(this, server, chain: null, SslPolicyErrors.None));
    }

    [Fact]
    public void A_server_from_the_pinned_authority_is_accepted()
    {
        using var pinned = Authority("CN=Kontena Test CA");
        using var server = LeafSignedBy(pinned, "docker.test");

        using var credentials = CredentialsPinnedTo(pinned);

        // The other half of the point: a daemon whose CA no root store knows must still connect, which
        // is the case Docker.DotNet.X509 leaves as "it fails, or you turn verification off".
        Assert.True(
            credentials.ValidateAgainstAuthority(
                this, server, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_name_mismatch_is_refused_whoever_signed_it()
    {
        using var pinned = Authority("CN=Kontena Test CA");
        using var server = LeafSignedBy(pinned, "docker.test");

        using var credentials = CredentialsPinnedTo(pinned);

        // The one error that means "this is not the host you asked for" — the CA being right does not
        // make it the right machine.
        Assert.False(
            credentials.ValidateAgainstAuthority(
                this, server, chain: null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void A_certificate_only_ca_pem_loads()
    {
        using var pinned = Authority("CN=Kontena Test CA");

        // A ca.pem holds a certificate and nothing else. FromDirectory used to read it with the
        // file overload that also looks for a private key in there, so every standard
        // DOCKER_CERT_PATH directory threw on the way in and no TLS endpoint with a CA connected
        // at all (KON-368). Reaching a usable credentials object is the whole assertion.
        using var credentials = CredentialsPinnedTo(pinned);

        Assert.True(credentials.IsTlsCredentials());
    }

    /// <summary>
    /// A leaf may never outlive the authority that signed it — .NET refuses to issue one that does,
    /// with "the requested notAfter value is later than issuerCertificate.NotAfter". This fixture used
    /// to read the clock separately for each certificate, so on a machine slow enough to cross a second
    /// between the two it could not build its own test data. It failed on CI and passed on a laptop.
    /// </summary>
    [Fact]
    public void A_leaf_never_outlives_the_authority_that_signed_it()
    {
        using var authority = Authority("CN=test-ca");
        using var leaf = LeafSignedBy(authority, "docker.invalid");

        Assert.True(
            leaf.NotAfter <= authority.NotAfter,
            $"leaf expires {leaf.NotAfter:O}, after its issuer at {authority.NotAfter:O}");
    }

    /// <summary>A DOCKER_CERT_PATH directory holding the given CA, plus a client pair to load.</summary>
    private static MutualTlsCredentials CredentialsPinnedTo(X509Certificate2 authority)
    {
        var directory = Directory.CreateTempSubdirectory("kontena-mtls-").FullName;

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var client = request.CreateSelfSigned(Yesterday, Tomorrow);

        File.WriteAllText(Path.Combine(directory, "ca.pem"), authority.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "cert.pem"), client.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());

        try
        {
            return MutualTlsCredentials.FromDirectory(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static X509Certificate2 Authority(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        return request.CreateSelfSigned(Yesterday, Tomorrow);
    }

    private static X509Certificate2 LeafSignedBy(X509Certificate2 issuer, string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

        return request.Create(issuer, Yesterday, Tomorrow, [1, 2, 3, 4, 5, 6, 7, 8]);
    }

    /// <summary>
    /// One clock reading for every certificate in this fixture.
    /// <para>
    /// These used to read <see cref="DateTimeOffset.UtcNow"/> on each access, so the leaf asked for a
    /// <c>notAfter</c> a moment later than the authority that signed it — and .NET refuses that
    /// outright: "the requested notAfter value is later than issuerCertificate.NotAfter". Generating a
    /// 2048-bit key between the two reads is enough to cross a second boundary, which made this fail on
    /// a loaded CI runner and pass on a quiet laptop.
    /// </para>
    /// </summary>
    private static readonly DateTimeOffset Reference = DateTimeOffset.UtcNow;

    private static DateTimeOffset Yesterday => Reference.AddDays(-1);

    private static DateTimeOffset Tomorrow => Reference.AddDays(1);
}
