using System.Net;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string? Body,
    string? ContentType);

internal sealed class QueueHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();

    public List<RecordedHttpRequest> Requests { get; } = [];

    public void Enqueue(HttpStatusCode status, string body) =>
        responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            body,
            request.Content?.Headers.ContentType?.ToString()));
        if (responses.Count == 0)
        {
            throw new InvalidOperationException("测试没有为 HTTP 请求配置响应。 ");
        }
        return responses.Dequeue()(request);
    }
}

internal sealed class UnknownLengthByteContent(byte[] data) : HttpContent
{
    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context) => stream.WriteAsync(data).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed class StubFeishuWebSocketEndpointProvider(
    FeishuWebSocketEndpoint endpoint) : IFeishuWebSocketEndpointProvider
{
    public int Calls { get; private set; }

    public Task<FeishuWebSocketEndpoint> GetAsync(
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(endpoint);
    }
}

internal sealed class QueueFeishuWebSocketConnection : IFeishuWebSocketConnection
{
    private readonly Queue<object?> receives = [];

    public List<byte[]> Sent { get; } = [];

    public int ConnectCalls { get; private set; }

    public bool Disposed { get; private set; }

    public void Enqueue(byte[]? frame) => receives.Enqueue(frame);

    public void Enqueue(Exception exception) => receives.Enqueue(exception);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receives.Count == 0)
        {
            return Task.FromResult<byte[]?>(null);
        }
        var next = receives.Dequeue();
        return next switch
        {
            Exception exception => Task.FromException<byte[]?>(exception),
            byte[] frame => Task.FromResult<byte[]?>(frame),
            null => Task.FromResult<byte[]?>(null),
            _ => throw new InvalidOperationException(),
        };
    }

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        Sent.Add(frame);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class QueueFeishuWebSocketConnectionFactory(
    params IFeishuWebSocketConnection[] connections)
    : IFeishuWebSocketConnectionFactory
{
    private readonly Queue<IFeishuWebSocketConnection> queue = new(connections);

    public int Created { get; private set; }

    public IFeishuWebSocketConnection Create()
    {
        Created++;
        return queue.Dequeue();
    }
}

internal sealed class FailingPingFeishuWebSocketConnection : IFeishuWebSocketConnection
{
    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }

    public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default) =>
        Task.FromException(new IOException("simulated ping failure"));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ListFeishuEventSource(
    params FeishuInboundEnvelope[] envelopes) : IFeishuEventSource
{
    public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return envelope;
        }
        await Task.CompletedTask;
    }
}

internal sealed class RecordingFeishuIntentSink : IFeishuIntentSink
{
    public List<FeishuIntent> Intents { get; } = [];

    public FeishuCallbackResult? Result { get; set; }

    public int FailuresRemaining { get; set; }

    public Task<FeishuCallbackResult?> PublishAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new InvalidOperationException("simulated sink failure");
        }
        Intents.Add(intent);
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingFeishuGateway : IFeishuGateway
{
    public List<(string MessageId, FeishuCardView Card)> Patches { get; } = [];

    public int FailPatchCount { get; set; }

    public Task<string> SendTextAsync(
        string chatId,
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("message-text");

    public Task<string> ReplyTextAsync(
        string messageId,
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("message-reply");

    public Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("message-card");

    public Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        CancellationToken cancellationToken = default)
    {
        if (FailPatchCount > 0)
        {
            FailPatchCount--;
            throw new HttpRequestException("simulated patch failure");
        }
        Patches.Add((messageId, card));
        return Task.CompletedTask;
    }

    public Task<FeishuSessionGroup> CreateSessionGroupAsync(
        string ownerOpenId,
        string name,
        string description,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new FeishuSessionGroup("chat-group", name));

    public Task UpdateSessionGroupNameAsync(
        string chatId,
        string name,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteSessionGroupAsync(
        string chatId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<long> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default) => Task.FromResult(0L);

    public Task<string> SendLocalFileAsync(
        string chatId,
        string filePath,
        CancellationToken cancellationToken = default) => Task.FromResult("file-message");
}
