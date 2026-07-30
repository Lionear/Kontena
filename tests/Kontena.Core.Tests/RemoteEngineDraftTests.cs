using Kontena.Sdk.Models;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// The form-to-model step. Two screens fill this in — the add wizard and Settings — so the rules about
/// which fields belong to which transport are tested here rather than trusted twice.
/// </summary>
public class RemoteEngineDraftTests
{
    [Fact]
    public void Ssh_drops_the_certificate_directory()
    {
        // Someone fills in the TCP form, switches to SSH, and submits. Carrying the path along would
        // store a certificate directory on an SSH engine, where it means nothing and reads as if it does.
        var remote = new RemoteEngineDraft
        {
            Host = "build-01",
            IsSsh = true,
            CertificateDirectory = "~/.docker/certs",
            AllowInsecure = true,
        }.Build("r1");

        Assert.Equal(RemoteEngineTransport.Ssh, remote.Transport);
        Assert.Null(remote.CertificateDirectory);
        Assert.False(remote.AllowInsecureTcp);
    }

    [Fact]
    public void Tcp_drops_the_user_and_socket_path()
    {
        var remote = new RemoteEngineDraft
        {
            Host = "build-01",
            IsSsh = false,
            User = "deploy",
            SocketPath = "/var/run/docker.sock",
            CertificateDirectory = "~/.docker/certs",
        }.Build("r1");

        Assert.Equal(RemoteEngineTransport.Tcp, remote.Transport);
        Assert.Null(remote.User);
        Assert.Null(remote.SocketPath);
        Assert.Equal("~/.docker/certs", remote.CertificateDirectory);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("not-a-port", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("2222", 2222)]
    public void An_unusable_port_means_the_transports_default(string typed, int? expected)
    {
        var remote = new RemoteEngineDraft { Host = "build-01", Port = typed }.Build("r1");
        Assert.Equal(expected, remote.Port);
    }

    [Fact]
    public void An_empty_name_falls_back_to_the_host()
    {
        // The switcher needs something to show, and the host is what the user just typed.
        var remote = new RemoteEngineDraft { Host = "build-01.example.com" }.Build("r1");
        Assert.Equal("build-01.example.com", remote.Name);
    }

    [Fact]
    public void Tcp_without_certificates_is_refused_unless_acknowledged()
    {
        var bare = new RemoteEngineDraft { Host = "build-01", IsSsh = false };
        Assert.NotNull(bare.Problem);

        // Same endpoint, explicitly acknowledged.
        Assert.Null((bare with { AllowInsecure = true }).Problem);

        // Same endpoint, with certificates instead.
        Assert.Null((bare with { CertificateDirectory = "~/.docker/certs" }).Problem);
    }

    [Fact]
    public void A_missing_host_is_a_problem_on_both_transports()
    {
        Assert.NotNull(new RemoteEngineDraft { IsSsh = true }.Problem);
        Assert.NotNull(new RemoteEngineDraft { IsSsh = false, AllowInsecure = true }.Problem);
    }

    [Fact]
    public void Insecure_is_only_claimed_for_tcp_without_certificates()
    {
        Assert.False(new RemoteEngineDraft { Host = "h", IsSsh = true }.IsInsecureTcp);
        Assert.True(new RemoteEngineDraft { Host = "h", IsSsh = false }.IsInsecureTcp);
        Assert.False(
            new RemoteEngineDraft { Host = "h", IsSsh = false, CertificateDirectory = "/c" }.IsInsecureTcp);
    }
}
