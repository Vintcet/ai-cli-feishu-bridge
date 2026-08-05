using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuWebSocketEventSourceTests
{
    [TestMethod]
    public async Task EndpointDiscoveryReadsServiceAndPingConfiguration()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "code":0,
              "data":{
                "URL":"wss://ws.feishu.test/connect?device_id=device-1&service_id=42",
                "ClientConfig":{"PingInterval":30}
              }
            }
            """);
        var provider = new HttpFeishuWebSocketEndpointProvider(
            new HttpClient(handler),
            new("app-id", "app-secret", new Uri("https://open.feishu.test/")));

        var endpoint = await provider.GetAsync();

        Assert.AreEqual("wss://ws.feishu.test/connect?device_id=device-1&service_id=42", endpoint.Url.ToString());
        Assert.AreEqual(42, endpoint.ServiceId);
        Assert.AreEqual(TimeSpan.FromSeconds(30), endpoint.PingInterval);
        Assert.AreEqual("/callback/ws/endpoint", handler.Requests[0].Uri.AbsolutePath);
        using var request = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.AreEqual("app-id", request.RootElement.GetProperty("AppID").GetString());
        Assert.AreEqual("app-secret", request.RootElement.GetProperty("AppSecret").GetString());
    }

    [TestMethod]
    public async Task SourceMergesEventAndSendsCallbackAckOnSameConnection()
    {
        var connection = new QueueFeishuWebSocketConnection();
        var payload = """
            {
              "schema":"2.0",
              "header":{"event_id":"event-1","event_type":"card.action.trigger"},
              "event":{"operator":{"open_id":"owner"}}
            }
            """;
        var midpoint = payload.Length / 2;
        connection.Enqueue(EncodedFrame(payload[..midpoint], 2, 0));
        connection.Enqueue(EncodedFrame(payload[midpoint..], 2, 1));
        var endpoint = new FeishuWebSocketEndpoint(
            new Uri("wss://ws.feishu.test/connect?service_id=7"),
            7,
            TimeSpan.FromHours(1));
        var source = new FeishuWebSocketEventSource(
            new StubFeishuWebSocketEndpointProvider(endpoint),
            new QueueFeishuWebSocketConnectionFactory(connection),
            TimeSpan.Zero);

        await using var enumerator = source.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await enumerator.MoveNextAsync());
        var envelope = enumerator.Current;
        Assert.AreEqual("event-1", envelope.EventId);
        Assert.AreEqual("card.action.trigger", envelope.EventType);
        await envelope.AcknowledgeAsync(new("success", "已处理"));

        Assert.IsTrue(connection.Sent.Count >= 2);
        var ack = connection.Sent
            .Select(frame => FeishuWireFrameCodec.Decode(frame))
            .Single(frame => frame.Method == 1);
        using var body = JsonDocument.Parse(ack.Payload);
        Assert.AreEqual(200, body.RootElement.GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task SourceReconnectsAfterWebSocketFailure()
    {
        var first = new QueueFeishuWebSocketConnection();
        first.Enqueue(new WebSocketException("simulated disconnect"));
        var second = new QueueFeishuWebSocketConnection();
        second.Enqueue(EncodedFrame(V2Message("event-after-reconnect"), 1, 0));
        var provider = new StubFeishuWebSocketEndpointProvider(new(
            new Uri("wss://ws.feishu.test/connect?service_id=7"),
            7,
            TimeSpan.FromHours(1)));
        var factory = new QueueFeishuWebSocketConnectionFactory(first, second);
        var source = new FeishuWebSocketEventSource(provider, factory, TimeSpan.Zero);

        await using var enumerator = source.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await enumerator.MoveNextAsync());

        Assert.AreEqual("event-after-reconnect", enumerator.Current.EventId);
        Assert.AreEqual(2, factory.Created);
        Assert.AreEqual(2, provider.Calls);
        Assert.IsTrue(first.Disposed);
    }

    [TestMethod]
    public async Task SourceReconnectsWhenPingFailsWhileReceiveIsBlocked()
    {
        var first = new FailingPingFeishuWebSocketConnection();
        var second = new QueueFeishuWebSocketConnection();
        second.Enqueue(EncodedFrame(V2Message("event-after-ping-failure"), 1, 0));
        var provider = new StubFeishuWebSocketEndpointProvider(new(
            new Uri("wss://ws.feishu.test/connect?service_id=7"),
            7,
            TimeSpan.FromHours(1)));
        var factory = new QueueFeishuWebSocketConnectionFactory(first, second);
        var source = new FeishuWebSocketEventSource(provider, factory, TimeSpan.Zero);

        await using var enumerator = source.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await enumerator.MoveNextAsync());

        Assert.AreEqual("event-after-ping-failure", enumerator.Current.EventId);
        Assert.AreEqual(2, factory.Created);
        Assert.IsTrue(first.Disposed);
    }

    private static byte[] EncodedFrame(string payload, int sum, int sequence) =>
        FeishuWireFrameCodec.Encode(new(
            1,
            2,
            7,
            1,
            [
                new(FeishuWebSocketHeaders.Type, FeishuWebSocketMessageTypes.Event),
                new(FeishuWebSocketHeaders.MessageId, "message-1"),
                new(FeishuWebSocketHeaders.TraceId, "trace-1"),
                new(FeishuWebSocketHeaders.Sum, sum.ToString()),
                new(FeishuWebSocketHeaders.Sequence, sequence.ToString()),
            ],
            "",
            "application/json",
            Encoding.UTF8.GetBytes(payload),
            ""));

    private static string V2Message(string eventId) => JsonSerializer.Serialize(new
    {
        schema = "2.0",
        header = new { event_id = eventId, event_type = "im.message.receive_v1" },
        @event = new
        {
            sender = new { sender_id = new { open_id = "owner" } },
            message = new
            {
                message_id = "message-1",
                chat_id = "chat-1",
                message_type = "text",
                content = "{\"text\":\"hello\"}",
            },
        },
    });
}
