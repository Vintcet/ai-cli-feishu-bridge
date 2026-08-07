using System.Net;
using System.Text;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveOpenCodeEventSourceTests
{
    private static readonly OpenCodeEndpoint Endpoint = new(
        new Uri("http://127.0.0.1:5100/"),
        "C:/repo space");

    [TestMethod]
    public async Task HealthProbeUsesGlobalEndpointAndMatchesNodeHealthSemantics()
    {
        var handler = new QueueHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"healthy\":false}"),
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var source = new ActiveOpenCodeEventSource(ActiveOptions(), client);

        Assert.IsFalse(await source.ProbeHealthAsync(Endpoint));
        Assert.IsTrue(await source.ProbeHealthAsync(Endpoint));
        Assert.IsFalse(await source.ProbeHealthAsync(Endpoint));

        Assert.AreEqual(3, handler.Requests.Count);
        Assert.IsTrue(handler.Requests.All(request =>
            request.Method == HttpMethod.Get &&
            request.Uri == new Uri("http://127.0.0.1:5100/global/health")));
    }

    [TestMethod]
    public async Task HealthProbeRejectsChunkedBodyPastLimitAndTimesOut()
    {
        var oversized = new QueueHandler();
        oversized.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(
                Encoding.UTF8.GetBytes("{\"padding\":\"too-large\"}"))),
        });
        using var oversizedClient = new HttpClient(oversized);
        using var limited = new ActiveOpenCodeEventSource(
            ActiveOptions(),
            oversizedClient,
            maximumHealthBodyBytes: 8);

        Assert.IsFalse(await limited.ProbeHealthAsync(Endpoint));

        using var timeoutClient = new HttpClient(new BlockingHandler());
        using var timeout = new ActiveOpenCodeEventSource(
            ActiveOptions(),
            timeoutClient,
            healthTimeout: TimeSpan.FromMilliseconds(20));

        Assert.IsFalse(await timeout.ProbeHealthAsync(Endpoint));
    }

    [TestMethod]
    public async Task EventStreamUsesValidatedLoopbackEndpointAndDirectoryScope()
    {
        var handler = new QueueHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"type\":\"session.idle\",\"properties\":{\"sessionID\":\"session-1\"}}\n\n"),
        });
        using var client = new HttpClient(handler);
        using var source = new ActiveOpenCodeEventSource(ActiveOptions(), client);

        var events = new List<OpenCodeRawEvent>();
        await foreach (var rawEvent in source.ReadAllAsync(Endpoint))
        {
            events.Add(rawEvent);
        }

        Assert.AreEqual("session-1", events.Single().Properties
            .GetProperty("sessionID").GetString());
        var request = handler.Requests.Single();
        Assert.AreEqual("/event", request.Uri.AbsolutePath);
        StringAssert.Contains(request.Uri.Query, "directory=C%3A%2Frepo%20space");
        CollectionAssert.Contains(request.Accept.ToArray(), "text/event-stream");
    }

    [TestMethod]
    public async Task EventOwnerFailsClosedBeforeRequestForInvalidModeOrOrigin()
    {
        var handler = new QueueHandler();
        using var client = new HttpClient(handler);
        using var source = new ActiveOpenCodeEventSource(ActiveOptions(), client);
        var invalid = new[]
        {
            new OpenCodeEndpoint(new Uri("https://127.0.0.1:5100/"), null),
            new OpenCodeEndpoint(new Uri("http://localhost:5100/"), null),
            new OpenCodeEndpoint(new Uri("http://127.0.0.1:5100/nested"), null),
            new OpenCodeEndpoint(new Uri("http://127.0.0.1:5100/?target=other"), null),
        };
        foreach (var endpoint in invalid)
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in source.ReadAllAsync(endpoint))
                {
                }
            });
        }

        using var passive = new ActiveOpenCodeEventSource(
            BridgeHostOptions.Passive(Path.GetTempPath()),
            client);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await passive.ProbeHealthAsync(Endpoint));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.GetTempPath(),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "opencode-event-source-test");

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyList<string> Accept);

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = [];
        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(
                request.Method,
                request.RequestUri!,
                request.Headers.Accept.Select(value => value.MediaType!).ToArray()));
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
