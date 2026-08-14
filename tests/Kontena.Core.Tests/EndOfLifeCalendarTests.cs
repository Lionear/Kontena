using System.Net;
using Kontena.Core.Versioning;
using Xunit;

namespace Kontena.Core.Tests;

/// <summary>
/// Reading a publisher's release calendar from endoflife.date (KON-370). Kontena keeps no table of its
/// own: it is not the vendor of any of these products, and a hand-kept list would go stale between
/// releases.
/// </summary>
public sealed class EndOfLifeCalendarTests
{
    private const string DockerEngine = """
        {
          "schema_version": "1.2.0",
          "result": {
            "name": "docker-engine",
            "label": "Docker Engine",
            "releases": [
              {
                "name": "29",
                "isMaintained": true,
                "eolFrom": null,
                "latest": { "name": "29.7.2", "date": "2026-07-30" }
              },
              {
                "name": "28",
                "isMaintained": false,
                "eolFrom": "2026-05-13",
                "latest": { "name": "28.5.2", "date": "2026-04-02" }
              }
            ]
          }
        }
        """;

    private sealed class FakeHandler(HttpStatusCode status, string? body, Exception? throws = null)
        : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);

            if (throws is not null)
                throw throws;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty),
            });
        }
    }

    [Fact]
    public async Task Every_published_cycle_is_read()
    {
        var calendar = new EndOfLifeCalendar(new HttpClient(new FakeHandler(HttpStatusCode.OK, DockerEngine)));

        var cycles = await calendar.CyclesAsync("docker-engine");

        Assert.Equal(
            [
                new ReleaseCycle("29", IsMaintained: true, EolFrom: null, Latest: "29.7.2"),
                new ReleaseCycle("28", IsMaintained: false, EolFrom: new DateOnly(2026, 5, 13), Latest: "28.5.2"),
            ],
            cycles);
    }

    [Fact]
    public async Task The_request_names_the_product_and_nothing_else()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, DockerEngine);

        await new EndOfLifeCalendar(new HttpClient(handler)).CyclesAsync("docker-engine");

        // The whole privacy argument for doing this at all: the request says which product is being
        // asked about, never which version is installed. One request, one product, no query string.
        var requested = Assert.Single(handler.Requested);
        Assert.Equal("https://endoflife.date/api/v1/products/docker-engine", requested.ToString());
    }

    [Fact]
    public async Task A_product_nobody_publishes_says_nothing()
    {
        var calendar = new EndOfLifeCalendar(new HttpClient(new FakeHandler(HttpStatusCode.NotFound, null)));

        Assert.Null(await calendar.CyclesAsync("apple-container"));
    }

    [Fact]
    public async Task Being_offline_says_nothing_rather_than_throwing()
    {
        var calendar = new EndOfLifeCalendar(
            new HttpClient(new FakeHandler(HttpStatusCode.OK, null, new HttpRequestException("no route"))));

        Assert.Null(await calendar.CyclesAsync("docker-engine"));
    }

    [Fact]
    public async Task A_document_that_cannot_be_read_says_nothing()
    {
        var calendar = new EndOfLifeCalendar(
            new HttpClient(new FakeHandler(HttpStatusCode.OK, "<html>we moved</html>")));

        // A gateway's error page is a 200 with the wrong body. Nothing is a better answer than a crash.
        Assert.Null(await calendar.CyclesAsync("docker-engine"));
    }
}
