using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class OpenCodeEventSourceTests
{
    [TestMethod]
    public async Task EventSourceUsesSseEndpointAndParsesStreamingFrames()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedReadStream(
                "data: {\"type\":\"session.idle\",",
                "\"properties\":{\"sessionID\":\"session-1\"}}\n\n")),
        });
        using var client = new HttpClient(handler);
        var source = new HttpOpenCodeEventSource(client);
        var endpoint = new OpenCodeEndpoint(
            new Uri("http://127.0.0.1:43210/"),
            "C:/repo space");

        var events = new List<OpenCodeRawEvent>();
        await foreach (var rawEvent in source.ReadAllAsync(endpoint))
        {
            events.Add(rawEvent);
        }

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("session.idle", events[0].Type);
        Assert.AreEqual("session-1", events[0].Properties
            .GetProperty("sessionID").GetString());
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("/event", request.Uri.AbsolutePath);
        StringAssert.Contains(request.Uri.Query, "directory=C%3A%2Frepo%20space");
        CollectionAssert.Contains(request.Accept.ToArray(), "text/event-stream");
    }

    [TestMethod]
    public async Task EventSourcePropagatesHttpFailure()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var source = new HttpOpenCodeEventSource(client);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in source.ReadAllAsync(
                               new(new Uri("http://127.0.0.1:43210/"), null)))
            {
            }
        });
    }

    private sealed class ChunkedReadStream(params string[] chunks) : Stream
    {
        private readonly Queue<byte[]> remaining = new(
            chunks.Select(chunk => Encoding.UTF8.GetBytes(chunk)));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!remaining.TryDequeue(out var chunk))
            {
                return ValueTask.FromResult(0);
            }
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
