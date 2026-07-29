using System.Net;
using System.Security.Authentication;
using Kontena.App.ViewModels;
using Kontena.Core.Models;

namespace Kontena.App.Tests;

/// <summary>
/// What the update card says when a check fails (KON-163). Every <c>HttpRequestException</c> used to
/// read "check your connection", which is right for one of the four ways this fails and sends the
/// reader after their own network for the other three.
/// <para>
/// The reported case was a 404: the nightly release is deleted and rebuilt on every run, so for a few
/// minutes a night the feed genuinely is not there. The publish side of that is fixed in
/// <c>build.yml</c>; this is the half that has to be honest when it happens anyway.
/// </para>
/// </summary>
public class UpdateMessageTests
{
    private static string Describe(HttpStatusCode? status, UpdateChannel channel) =>
        UpdateViewModel.Describe(new HttpRequestException("boom", null, status), channel);

    [Theory]
    [InlineData(UpdateChannel.Nightly)]
    [InlineData(UpdateChannel.Preview)]
    public void A_missing_feed_on_a_rolling_channel_says_it_is_being_replaced(UpdateChannel channel)
    {
        var message = Describe(HttpStatusCode.NotFound, channel);

        // The reader has nothing to do, and the message must not imply otherwise.
        Assert.Contains("try again in a few minutes", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your connection", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_feed_on_stable_says_nothing_has_been_published()
    {
        // Stable is not republished, so the same 404 means something else entirely.
        var message = Describe(HttpStatusCode.NotFound, UpdateChannel.Stable);

        Assert.Contains("published", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your connection", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void A_rate_limit_says_to_wait_rather_than_to_check_the_network(HttpStatusCode status)
    {
        var message = Describe(status, UpdateChannel.Stable);

        Assert.Contains("later", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your connection", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_refused_tls_handshake_names_the_thing_in_between()
    {
        var error = new HttpRequestException("failed", new AuthenticationException("no"));

        var message = UpdateViewModel.Describe(error, UpdateChannel.Stable);

        Assert.Contains("proxy", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_answer_at_all_is_still_the_connection()
    {
        // The one case the old sentence was right about, and it keeps it.
        var message = UpdateViewModel.Describe(new HttpRequestException("no route to host"), UpdateChannel.Stable);

        Assert.Contains("Check your connection", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_status_is_reported_with_its_number()
    {
        var message = Describe(HttpStatusCode.BadGateway, UpdateChannel.Stable);

        Assert.Contains("502", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disk_failure_still_reads_as_one()
    {
        var message = UpdateViewModel.Describe(new IOException("No space left on device"), UpdateChannel.Nightly);

        Assert.Contains("No space left on device", message, StringComparison.Ordinal);
    }
}
