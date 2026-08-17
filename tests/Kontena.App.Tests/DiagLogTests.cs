using Kontena.App.Services;

namespace Kontena.App.Tests;

/// <summary>
/// The diagnostic log keeps one generation and nothing it was not given (KON-389). Both halves are
/// promises to the person who switched it on: that the session before the one they are reading is
/// still there, and that sending the file to a maintainer does not send their credentials with it.
/// </summary>
[Collection("Diag")]
public sealed class DiagLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kontena-diag-{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "diagnostics.log");

    public void Dispose()
    {
        DiagLog.Close();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_nothing_until_it_is_opened()
    {
        // The state every run is in, and the one the setting's default leaves it in.
        Assert.False(DiagLog.IsOpen);
        Assert.False(Diag.Enabled);

        Diag.Action("start container", "abc123");
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void Records_the_action_and_the_backend_it_ran_against()
    {
        DiagLog.Open(LogPath);
        Diag.Context = "kubernetes:kind-kind";
        try
        {
            Diag.Action("delete Deployment", "default/web");
        }
        finally
        {
            Diag.Context = string.Empty;
        }

        DiagLog.Close();

        var log = File.ReadAllText(LogPath);
        Assert.Contains("delete Deployment — default/web", log, StringComparison.Ordinal);
        Assert.Contains("kubernetes:kind-kind", log, StringComparison.Ordinal);

        // The memory sample is written when the log opens, not only every half minute — a session
        // that crashes in its first ten seconds still has to say how much it was holding.
        Assert.Contains("memory — working set", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Archives_the_previous_session_and_keeps_only_that_one()
    {
        DiagLog.Open(LogPath);
        Diag.Action("first session");
        DiagLog.Close();

        DiagLog.Open(LogPath);
        Diag.Action("second session");
        DiagLog.Close();

        Assert.Contains("first session", File.ReadAllText(LogPath + DiagLog.PreviousSuffix), StringComparison.Ordinal);
        Assert.Contains("second session", File.ReadAllText(LogPath), StringComparison.Ordinal);

        // A third run replaces the archive rather than adding to it — one generation, so the
        // directory cannot grow on its own.
        DiagLog.Open(LogPath);
        Diag.Action("third session");
        DiagLog.Close();

        var previous = File.ReadAllText(LogPath + DiagLog.PreviousSuffix);
        Assert.Contains("second session", previous, StringComparison.Ordinal);
        Assert.DoesNotContain("first session", previous, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Theory]
    // A password an engine handed back inside a URL.
    [InlineData("registry https://admin:hunter2@registry.example.com/v2", "hunter2")]
    // The shapes a credential turns up in when something else formatted the string.
    [InlineData("failed: password=hunter2", "hunter2")]
    [InlineData("failed: Authorization: Bearer abcdefghijklmnop", "abcdefghijklmnop")]
    [InlineData("token = eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.dBjftJeZ4CVPmB92K27u", "eyJhbGciOiJIUzI1NiJ9")]
    // A key that arrived with no name in front of it at all.
    [InlineData("secret data AbCdEf0123456789AbCdEf0123456789AbCd=", "AbCdEf0123456789AbCdEf0123456789AbCd")]
    public void Strips_anything_credential_shaped(string line, string mustNotSurvive)
    {
        var redacted = DiagLog.Redact(line);

        Assert.DoesNotContain(mustNotSurvive, redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_the_ids_the_log_exists_to_record()
    {
        // Container and image ids are 64 lowercase hex characters. Redacting those would leave a log
        // that records that something was deleted and refuses to say what.
        const string line =
            "stop container: 3f2a9b8c7d6e5f40312233445566778899aabbccddeeff00112233445566778f "
            + "image sha256:9e83e05fef2f4bd0c62a2e30e93d4e0e2b5b0b6f2e8d3c1a0f9e8d7c6b5a4938";

        Assert.Equal(line, DiagLog.Redact(line));
    }
}
