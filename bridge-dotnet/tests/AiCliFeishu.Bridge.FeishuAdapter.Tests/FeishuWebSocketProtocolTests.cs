using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.FeishuAdapter.Tests;

[TestClass]
public sealed class FeishuWebSocketProtocolTests
{
    [TestMethod]
    public void ProtobufFrameRoundTripsAllSdkFieldNumbers()
    {
        var frame = new FeishuWireFrame(
            123,
            456,
            7,
            1,
            [new("type", "event"), new("message_id", "message-1")],
            "identity",
            "application/json",
            [1, 2, 3],
            "log-new");

        var encoded = FeishuWireFrameCodec.Encode(frame);
        var decoded = FeishuWireFrameCodec.Decode(encoded);

        Assert.AreEqual(frame.SequenceId, decoded.SequenceId);
        Assert.AreEqual(frame.LogId, decoded.LogId);
        Assert.AreEqual(frame.Service, decoded.Service);
        Assert.AreEqual(frame.Method, decoded.Method);
        CollectionAssert.AreEqual(frame.Headers.ToArray(), decoded.Headers.ToArray());
        Assert.AreEqual(frame.PayloadEncoding, decoded.PayloadEncoding);
        Assert.AreEqual(frame.PayloadType, decoded.PayloadType);
        CollectionAssert.AreEqual(frame.Payload, decoded.Payload);
        Assert.AreEqual(frame.LogIdNew, decoded.LogIdNew);
    }

    [TestMethod]
    public void FragmentAssemblerUsesZeroBasedSequenceAndIgnoresDuplicateChunks()
    {
        var assembler = new FeishuWebSocketFragmentAssembler();

        Assert.IsNull(assembler.Add(Frame("message-1", "trace-1", 3, 2, "C")));
        Assert.IsNull(assembler.Add(Frame("message-1", "trace-1", 3, 0, "A")));
        Assert.IsNull(assembler.Add(Frame("message-1", "trace-1", 3, 0, "ignored")));
        var merged = assembler.Add(Frame("message-1", "trace-1", 3, 1, "B"));

        Assert.IsNotNull(merged);
        Assert.AreEqual("ABC", Encoding.UTF8.GetString(merged.Payload));
        Assert.AreEqual("message-1", merged.MessageId);
        Assert.AreEqual("trace-1", merged.TraceId);
    }

    [TestMethod]
    public void FragmentAssemblerRejectsSequenceOutsideDeclaredSum()
    {
        var assembler = new FeishuWebSocketFragmentAssembler();

        Assert.ThrowsException<InvalidDataException>(() =>
            assembler.Add(Frame("message-1", "trace-1", 2, 2, "bad")));
    }

    [TestMethod]
    public void V2AndV1EventEnvelopesNormalizeToStableMetadata()
    {
        var v2 = FeishuWebSocketEnvelopeParser.Parse(Merged("""
            {
              "schema":"2.0",
              "header":{"event_id":"event-v2","event_type":"im.message.receive_v1"},
              "event":{"message":{"message_id":"message-v2"}}
            }
            """));
        var v1 = FeishuWebSocketEnvelopeParser.Parse(Merged("""
            {
              "uuid":"event-v1",
              "event":{"type":"card.action.trigger","open_message_id":"card-1"}
            }
            """));

        Assert.AreEqual("event-v2", v2.EventId);
        Assert.AreEqual("im.message.receive_v1", v2.EventType);
        Assert.AreEqual(
            "message-v2",
            v2.Payload.GetProperty("message").GetProperty("message_id").GetString());
        Assert.AreEqual("event-v1", v1.EventId);
        Assert.AreEqual("card.action.trigger", v1.EventType);
    }

    [TestMethod]
    public void AckPayloadMatchesSdkAndCallbackResultIsBase64Json()
    {
        var message = Merged("{}", Frame("message-1", "trace-1", 1, 0, "{}"));
        var callback = new FeishuCallbackResult(
            "success",
            "已处理",
            new(new JsonObject { ["config"] = new JsonObject { ["update_multi"] = true } }));

        var response = FeishuWebSocketEnvelopeParser.Response(message, callback, 200, 42);

        Assert.AreEqual("42", response.Header(FeishuWebSocketHeaders.BusinessRuntime));
        using var ack = JsonDocument.Parse(response.Payload);
        Assert.AreEqual(200, ack.RootElement.GetProperty("code").GetInt32());
        var data = Convert.FromBase64String(ack.RootElement.GetProperty("data").GetString()!);
        using var result = JsonDocument.Parse(data);
        Assert.AreEqual(
            "success",
            result.RootElement.GetProperty("toast").GetProperty("type").GetString());
        Assert.IsTrue(
            result.RootElement.GetProperty("card").GetProperty("config")
                .GetProperty("update_multi").GetBoolean());
    }

    [TestMethod]
    public void PlainAckContainsOnlyHttpCode()
    {
        var response = FeishuWebSocketEnvelopeParser.Response(Merged("{}"), null, 500, 0);
        using var ack = JsonDocument.Parse(response.Payload);

        Assert.AreEqual(500, ack.RootElement.GetProperty("code").GetInt32());
        Assert.IsFalse(ack.RootElement.TryGetProperty("data", out _));
    }

    private static FeishuWireFrame Frame(
        string messageId,
        string traceId,
        int sum,
        int sequence,
        string payload) => new(
            1,
            2,
            3,
            1,
            [
                new(FeishuWebSocketHeaders.Type, FeishuWebSocketMessageTypes.Event),
                new(FeishuWebSocketHeaders.MessageId, messageId),
                new(FeishuWebSocketHeaders.TraceId, traceId),
                new(FeishuWebSocketHeaders.Sum, sum.ToString()),
                new(FeishuWebSocketHeaders.Sequence, sequence.ToString()),
            ],
            "",
            "application/json",
            Encoding.UTF8.GetBytes(payload),
            "");

    private static FeishuMergedWebSocketEvent Merged(
        string payload,
        FeishuWireFrame? responseFrame = null) => new(
            responseFrame ?? Frame("message-1", "trace-1", 1, 0, payload),
            "message-1",
            "trace-1",
            Encoding.UTF8.GetBytes(payload));
}
