using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuEventPumpTests
{
    [TestMethod]
    public async Task PumpPublishesAcceptedIntentAndAcknowledgesCallbackResult()
    {
        FeishuCallbackResult? callback = null;
        var status = 0;
        var envelope = MessageEnvelope(
            "event-1",
            (result, code, _) =>
            {
                callback = result;
                status = code;
                return Task.CompletedTask;
            });
        var sink = new RecordingFeishuIntentSink
        {
            Result = new("success", "已收到"),
        };
        var pump = new FeishuEventPump(
            new ListFeishuEventSource(envelope),
            new(new InMemoryFeishuInboundDeduplicator()),
            sink);

        await pump.RunAsync();

        Assert.AreEqual(1, sink.Intents.Count);
        Assert.AreEqual(FeishuIntentTypes.MessagePrompt, sink.Intents[0].IntentType);
        Assert.AreEqual(200, status);
        Assert.AreEqual("已收到", callback!.ToastContent);
    }

    [TestMethod]
    public async Task PumpRunsFollowUpOnlyAfterAcknowledgementCompletes()
    {
        var order = new List<string>();
        var envelope = MessageEnvelope(
            "event-follow-up",
            (_, _, _) =>
            {
                order.Add("acknowledged");
                return Task.CompletedTask;
            });
        var sink = new RecordingFeishuIntentSink
        {
            Result = new(
                "warning",
                "已处理",
                AfterAcknowledged: _ =>
                {
                    order.Add("follow-up");
                    return Task.CompletedTask;
                }),
        };
        var pump = new FeishuEventPump(
            new ListFeishuEventSource(envelope),
            new(new InMemoryFeishuInboundDeduplicator()),
            sink);

        await pump.RunAsync();

        CollectionAssert.AreEqual(
            new[] { "acknowledged", "follow-up" },
            order);
    }

    [TestMethod]
    public async Task PumpAcknowledgesDuplicateWithoutPublishingTwice()
    {
        var statuses = new List<int>();
        var source = new ListFeishuEventSource(
            MessageEnvelope("same-event", (_, code, _) =>
            {
                statuses.Add(code);
                return Task.CompletedTask;
            }),
            MessageEnvelope("same-event", (_, code, _) =>
            {
                statuses.Add(code);
                return Task.CompletedTask;
            }));
        var sink = new RecordingFeishuIntentSink();
        var pump = new FeishuEventPump(
            source,
            new(new InMemoryFeishuInboundDeduplicator()),
            sink);

        await pump.RunAsync();

        Assert.AreEqual(1, sink.Intents.Count);
        CollectionAssert.AreEqual(new[] { 200, 200 }, statuses);
    }

    [TestMethod]
    public async Task PumpRejectsSinkFailureAndEnvelopeCompletesOnlyOnce()
    {
        var completions = 0;
        var status = 0;
        var envelope = MessageEnvelope(
            "event-failure",
            (_, code, _) =>
            {
                completions++;
                status = code;
                return Task.CompletedTask;
            });
        var sink = new RecordingFeishuIntentSink { FailuresRemaining = 1 };
        var pump = new FeishuEventPump(
            new ListFeishuEventSource(envelope),
            new(new InMemoryFeishuInboundDeduplicator()),
            sink);

        await pump.RunAsync();
        await envelope.AcknowledgeAsync();

        Assert.AreEqual(1, completions);
        Assert.AreEqual(500, status);
    }

    [TestMethod]
    public async Task SinkFailureReleasesClaimSoFeishuRedeliveryCanSucceed()
    {
        var statuses = new List<int>();
        var source = new ListFeishuEventSource(
            MessageEnvelope("retry-event", (_, code, _) =>
            {
                statuses.Add(code);
                return Task.CompletedTask;
            }),
            MessageEnvelope("retry-event", (_, code, _) =>
            {
                statuses.Add(code);
                return Task.CompletedTask;
            }));
        var sink = new RecordingFeishuIntentSink { FailuresRemaining = 1 };
        var pump = new FeishuEventPump(
            source,
            new(new InMemoryFeishuInboundDeduplicator()),
            sink);

        await pump.RunAsync();

        Assert.AreEqual(1, sink.Intents.Count);
        CollectionAssert.AreEqual(new[] { 500, 200 }, statuses);
    }

    [TestMethod]
    public async Task AckTransportFailureDoesNotStopFollowingEvents()
    {
        var secondStatus = 0;
        var source = new ListFeishuEventSource(
            MessageEnvelope("event-1", (_, _, _) =>
                Task.FromException(new IOException("simulated ack failure"))),
            MessageEnvelope("event-2", (_, code, _) =>
            {
                secondStatus = code;
                return Task.CompletedTask;
            }));
        var sink = new RecordingFeishuIntentSink();
        var pump = new FeishuEventPump(
            source,
            new(new InMemoryFeishuInboundDeduplicator()),
            sink);

        await pump.RunAsync();

        Assert.AreEqual(2, sink.Intents.Count);
        Assert.AreEqual(200, secondStatus);
    }

    private static FeishuInboundEnvelope MessageEnvelope(
        string eventId,
        Func<FeishuCallbackResult?, int, CancellationToken, Task> complete)
    {
        using var document = JsonDocument.Parse("""
            {
              "sender":{"sender_id":{"open_id":"owner"}},
              "message":{
                "message_id":"message-1",
                "chat_id":"chat-1",
                "chat_type":"group",
                "message_type":"text",
                "content":"{\"text\":\"hello\"}"
              }
            }
            """);
        return new(
            eventId,
            "trace-1",
            "im.message.receive_v1",
            document.RootElement,
            complete);
    }
}
