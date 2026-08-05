using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public sealed record FeishuWebSocketOptions(
    string AppId,
    string AppSecret,
    Uri BaseUri,
    TimeSpan? ReconnectDelay = null,
    TimeSpan? DefaultPingInterval = null);

public sealed record FeishuWebSocketEndpoint(
    Uri Url,
    int ServiceId,
    TimeSpan PingInterval);

public interface IFeishuWebSocketEndpointProvider
{
    Task<FeishuWebSocketEndpoint> GetAsync(
        CancellationToken cancellationToken = default);
}

public interface IFeishuWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default);

    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);

    Task SendAsync(byte[] frame, CancellationToken cancellationToken = default);
}

public interface IFeishuWebSocketConnectionFactory
{
    IFeishuWebSocketConnection Create();
}

public sealed class HttpFeishuWebSocketEndpointProvider(
    HttpClient http,
    FeishuWebSocketOptions options) : IFeishuWebSocketEndpointProvider
{
    public async Task<FeishuWebSocketEndpoint> GetAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(
            new Uri(options.BaseUri, "callback/ws/endpoint"),
            JsonContent.Create(new JsonObject
            {
                ["AppID"] = options.AppId,
                ["AppSecret"] = options.AppSecret,
            }),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"获取飞书 WebSocket 端点失败：HTTP {(int)response.StatusCode} {body}".Trim());
        }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeNode) && codeNode.TryGetInt32(out var value)
            ? value
            : -1;
        if (code != 0 || !root.TryGetProperty("data", out var data))
        {
            var message = root.TryGetProperty("msg", out var messageNode)
                ? messageNode.GetString()
                : "unknown error";
            throw new HttpRequestException($"获取飞书 WebSocket 端点失败：{code} {message}");
        }
        var urlText = data.TryGetProperty("URL", out var urlNode)
            ? urlNode.GetString()
            : null;
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
        {
            throw new HttpRequestException("飞书 WebSocket 端点响应缺少有效 URL。 ");
        }
        var query = ParseQuery(url.Query);
        if (!query.TryGetValue("service_id", out var serviceText) ||
            !int.TryParse(serviceText, out var serviceId))
        {
            throw new HttpRequestException("飞书 WebSocket URL 缺少 service_id。 ");
        }
        var pingInterval = options.DefaultPingInterval ?? TimeSpan.FromSeconds(120);
        if (data.TryGetProperty("ClientConfig", out var config) &&
            config.TryGetProperty("PingInterval", out var pingNode) &&
            pingNode.TryGetDouble(out var seconds) && seconds > 0)
        {
            pingInterval = TimeSpan.FromSeconds(seconds);
        }
        return new(url, serviceId, pingInterval);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[Uri.UnescapeDataString(pair[0])] = pair.Length == 2
                ? Uri.UnescapeDataString(pair[1])
                : "";
        }
        return values;
    }
}

public sealed class ClientFeishuWebSocketConnection : IFeishuWebSocketConnection
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default) =>
        socket.ConnectAsync(uri, cancellationToken);

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidDataException("飞书 WebSocket 返回了非二进制帧。 ");
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return stream.ToArray();
            }
        }
    }

    public async Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                frame,
                WebSocketMessageType.Binary,
                true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "bridge shutdown",
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
                socket.Abort();
            }
        }
        socket.Dispose();
        sendLock.Dispose();
    }
}

public sealed class ClientFeishuWebSocketConnectionFactory
    : IFeishuWebSocketConnectionFactory
{
    public IFeishuWebSocketConnection Create() => new ClientFeishuWebSocketConnection();
}

public sealed class FeishuWebSocketEventSource(
    IFeishuWebSocketEndpointProvider endpoints,
    IFeishuWebSocketConnectionFactory connections,
    TimeSpan? reconnectDelay = null) : IFeishuEventSource
{
    private readonly TimeSpan retryDelay = reconnectDelay ?? TimeSpan.FromSeconds(1);

    public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var opened = await OpenAsync(cancellationToken);
            if (opened.Cancelled)
            {
                yield break;
            }
            if (opened.Connection is null || opened.Endpoint is null)
            {
                if (!await DelayBeforeReconnectAsync(cancellationToken))
                {
                    yield break;
                }
                continue;
            }

            await using var connection = opened.Connection;
            using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var pingTask = PingAsync(connection, opened.Endpoint, connectionCancellation.Token);
            var assembler = new FeishuWebSocketFragmentAssembler();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var receiveTask = ReceiveNextAsync(
                        connection,
                        assembler,
                        connectionCancellation.Token);
                    if (await Task.WhenAny(receiveTask, pingTask) == pingTask)
                    {
                        connectionCancellation.Cancel();
                        await IgnoreConnectionCompletionAsync(receiveTask);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            yield break;
                        }
                        break;
                    }
                    var received = await receiveTask;
                    if (received.Cancelled)
                    {
                        yield break;
                    }
                    if (received.Reconnect)
                    {
                        break;
                    }
                    if (received.Envelope is not null)
                    {
                        yield return received.Envelope;
                    }
                }
            }
            finally
            {
                connectionCancellation.Cancel();
                await IgnoreConnectionCompletionAsync(pingTask);
            }
            if (!await DelayBeforeReconnectAsync(cancellationToken))
            {
                yield break;
            }
        }
    }

    private async Task<OpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        IFeishuWebSocketConnection? connection = null;
        try
        {
            var endpoint = await endpoints.GetAsync(cancellationToken);
            connection = connections.Create();
            await connection.ConnectAsync(endpoint.Url, cancellationToken);
            return new(endpoint, connection, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            return new(null, null, true);
        }
        catch (Exception exception) when (IsReconnectable(exception))
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            return new(null, null, false);
        }
    }

    private static async Task<ReceiveResult> ReceiveNextAsync(
        IFeishuWebSocketConnection connection,
        FeishuWebSocketFragmentAssembler assembler,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await connection.ReceiveAsync(cancellationToken);
            if (data is null)
            {
                return new(null, true, false);
            }
            var frame = FeishuWireFrameCodec.Decode(data);
            var merged = assembler.Add(frame);
            if (merged is null)
            {
                return new(null, false, false);
            }
            var parsed = FeishuWebSocketEnvelopeParser.Parse(merged);
            var startedAt = DateTimeOffset.UtcNow;
            return new(
                new(
                    parsed.EventId,
                    merged.TraceId,
                    parsed.EventType,
                    parsed.Payload,
                    async (result, statusCode, token) =>
                    {
                        var elapsed = DateTimeOffset.UtcNow - startedAt;
                        var response = FeishuWebSocketEnvelopeParser.Response(
                            merged,
                            result,
                            statusCode,
                            (long)elapsed.TotalMilliseconds);
                        await connection.SendAsync(
                            FeishuWireFrameCodec.Encode(response),
                            token);
                    }),
                false,
                false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(null, false, true);
        }
        catch (Exception exception) when (IsReconnectable(exception))
        {
            return new(null, true, false);
        }
    }

    private async Task<bool> DelayBeforeReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(retryDelay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool IsReconnectable(Exception exception) =>
        exception is HttpRequestException or WebSocketException or IOException or
            InvalidDataException or JsonException or ObjectDisposedException;

    private static async Task PingAsync(
        IFeishuWebSocketConnection connection,
        FeishuWebSocketEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(endpoint.PingInterval);
        do
        {
            var ping = new FeishuWireFrame(
                0,
                0,
                endpoint.ServiceId,
                0,
                [new(FeishuWebSocketHeaders.Type, FeishuWebSocketMessageTypes.Ping)],
                "",
                "",
                [],
                "");
            await connection.SendAsync(FeishuWireFrameCodec.Encode(ping), cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private static async Task IgnoreConnectionCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or WebSocketException or
                IOException or ObjectDisposedException)
        {
            // Expected while reconnecting or shutting down.
        }
    }

    private sealed record OpenResult(
        FeishuWebSocketEndpoint? Endpoint,
        IFeishuWebSocketConnection? Connection,
        bool Cancelled);

    private sealed record ReceiveResult(
        FeishuInboundEnvelope? Envelope,
        bool Reconnect,
        bool Cancelled);
}

public sealed class FeishuEventPump(
    IFeishuEventSource source,
    FeishuEventNormalizer normalizer,
    IFeishuIntentSink sink)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in source.ReadAllAsync(cancellationToken))
        {
            FeishuNormalizationResult normalized;
            try
            {
                normalized = envelope.EventType switch
                {
                    "im.message.receive_v1" => normalizer.NormalizeMessage(
                        envelope.EventId,
                        envelope.TraceId,
                        envelope.Payload),
                    "card.action.trigger" => normalizer.NormalizeCardAction(
                        envelope.EventId,
                        envelope.TraceId,
                        envelope.Payload),
                    _ => FeishuNormalizationResult.Rejected(
                        $"未注册的飞书事件：{envelope.EventType}"),
                };
            }
            catch
            {
                normalizer.Release(envelope.EventId);
                await TryRejectAsync(envelope, cancellationToken);
                continue;
            }
            if (normalized.Duplicate || normalized.Intent is null)
            {
                await TryAcknowledgeAsync(envelope, null, cancellationToken);
                continue;
            }

            FeishuCallbackResult? result;
            try
            {
                result = await sink.PublishAsync(normalized.Intent, cancellationToken);
            }
            catch
            {
                normalizer.Release(envelope.EventId);
                await TryRejectAsync(envelope, cancellationToken);
                continue;
            }
            await TryAcknowledgeAsync(envelope, result, cancellationToken);
        }
    }

    private static async Task TryAcknowledgeAsync(
        FeishuInboundEnvelope envelope,
        FeishuCallbackResult? result,
        CancellationToken cancellationToken)
    {
        try
        {
            await envelope.AcknowledgeAsync(result, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // The event is already handled. Feishu may redeliver it, and the inbound
            // claim will turn that redelivery into a plain acknowledgement.
        }
    }

    private static async Task TryRejectAsync(
        FeishuInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await envelope.RejectAsync(cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // A broken callback connection is recovered by the event source. The
            // released inbound claim allows Feishu's redelivery to be processed.
        }
    }
}
