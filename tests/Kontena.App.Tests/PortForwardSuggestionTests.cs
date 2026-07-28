using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// The local side of a port forward (KON-170). The dialog used to set the local port equal to the
/// remote one, which on Linux and macOS makes every well-known port a suggestion that cannot work:
/// binding below 1024 needs root, so "80 → 80" was an offer guaranteed to fail.
/// </summary>
public sealed class PortForwardSuggestionTests
{
    private static bool NothingTaken(int _) => false;

    [Fact]
    public void An_unprivileged_port_is_mirrored()
    {
        // 8080 → 8080 is what people expect and what they will remember typing into a browser.
        Assert.Equal(8080, LocalPortSuggestion.For(8080, NothingTaken));
    }

    [Theory]
    [InlineData(80, 8080)]
    [InlineData(443, 8443)]
    [InlineData(22, 8022)]
    public void A_privileged_port_shifts_instead_of_mirroring(int remote, int expected)
    {
        // The +8000 convention, so the familiar pairs come out right rather than arbitrary.
        Assert.Equal(expected, LocalPortSuggestion.For(remote, NothingTaken));
    }

    [Fact]
    public void A_local_port_already_forwarding_is_stepped_over()
    {
        // Suggesting a port this app is itself using produces an "address in use" the user did nothing
        // to deserve.
        Assert.Equal(8082, LocalPortSuggestion.For(8080, p => p is 8080 or 8081));
    }

    [Fact]
    public void Nothing_is_suggested_when_no_remote_port_is_known()
    {
        // Zero keeps Start disabled. The old behaviour invented 80 here and presented it with the same
        // confidence as a port that had actually been read off the resource.
        Assert.Equal(0, LocalPortSuggestion.For(0, NothingTaken));
    }

    [Fact]
    public void A_run_of_taken_ports_gives_up_rather_than_climbing_forever()
    {
        // Everything taken: fall back to the remote port and let the start attempt report the clash
        // with a message that names it, instead of spinning through 64k probes.
        Assert.Equal(8080, LocalPortSuggestion.For(8080, _ => true));
    }
}
