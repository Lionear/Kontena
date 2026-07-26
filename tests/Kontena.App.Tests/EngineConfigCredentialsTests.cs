using Kontena.App.Services;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// Reading the engine's own config. Kontena never writes these files, so every test here is about
/// understanding what another tool wrote — including the shapes that carry no secret at all.
/// </summary>
public class EngineConfigCredentialsTests
{
    [Fact]
    public void Reads_an_embedded_login()
    {
        // What `docker login` writes for a plain registry: base64 of "user:secret".
        const string json = """
            {"auths":{"ghcr.io":{"auth":"b2N0bzpnaHBfdG9rZW4="}}}
            """;

        var config = EngineConfigCredentials.Parse(json);

        var entry = Assert.Single(config.Auths);
        Assert.Equal("ghcr.io", entry.Host);
        Assert.Equal("octo", entry.Username);
        Assert.Equal("ghp_token", entry.Secret);
    }

    [Fact]
    public void A_password_containing_a_colon_survives()
    {
        // "octo:pass:word" — splitting on every colon would silently hand the registry half a password.
        var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("octo:pass:word"));

        var (username, secret) = EngineConfigCredentials.DecodeAuth(auth);

        Assert.Equal("octo", username);
        Assert.Equal("pass:word", secret);
    }

    [Fact]
    public void Garbage_in_the_auth_field_is_not_a_crash() =>
        Assert.Equal((null, null), EngineConfigCredentials.DecodeAuth("not base64 at all"));

    [Fact]
    public void Reads_the_helper_arrangement_instead_of_a_secret()
    {
        // Docker Desktop and the Linux keyring helpers write this: no secret in the file, a helper named
        // instead. Not understanding it is how a logged-in user still gets "pull access denied".
        const string json = """
            {"auths":{"https://index.docker.io/v1/":{}},"credsStore":"desktop",
             "credHelpers":{"gcr.io":"gcloud"}}
            """;

        var config = EngineConfigCredentials.Parse(json);

        Assert.Equal("desktop", config.CredsStore);
        Assert.Equal("gcloud", config.CredHelpers["gcr.io"]);

        // The Hub entry is present but carries nothing — the helper holds it.
        var hub = Assert.Single(config.Auths);
        Assert.Null(hub.Secret);
    }

    [Fact]
    public void A_malformed_config_reads_as_no_credentials()
    {
        // This file is written by other programs and versions; a surprise in it must not stop Kontena.
        var config = EngineConfigCredentials.Parse("{ this is not json");

        Assert.Empty(config.Auths);
        Assert.Null(config.CredsStore);
    }

    [Fact]
    public void An_empty_config_reads_as_no_credentials()
    {
        var config = EngineConfigCredentials.Parse("{}");

        Assert.Empty(config.Auths);
        Assert.Empty(config.CredHelpers);
    }

    [Fact]
    public void Lists_hub_under_its_canonical_name()
    {
        // config.json spells Hub as the legacy v1 URL. Listed as-is it would never match a pull of "nginx".
        var path = Path.Combine(Path.GetTempPath(), "kontena-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {"auths":{"https://index.docker.io/v1/":{"auth":"b2N0bzpodW50ZXIy"}}}
            """);

        try
        {
            var logins = new EngineConfigCredentials([path]).List();

            var login = Assert.Single(logins);
            Assert.Equal("docker.io", login.Host);
            Assert.Equal("octo", login.Username);
            Assert.Equal(RegistryCredentialSource.EngineConfig, login.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_hub_login_in_the_config_answers_a_pull_of_an_unqualified_image()
    {
        var path = Path.Combine(Path.GetTempPath(), "kontena-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {"auths":{"https://index.docker.io/v1/":{"auth":"b2N0bzpodW50ZXIy"}}}
            """);

        try
        {
            var credential = new EngineConfigCredentials([path]).Get(RegistryHost.For("nginx:1.27"));

            Assert.NotNull(credential);
            Assert.Equal("docker.io", credential!.Host);
            Assert.Equal("hunter2", credential.Secret);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_files_are_not_an_error() =>
        Assert.Empty(new EngineConfigCredentials(["/nonexistent/config.json"]).List());
}
