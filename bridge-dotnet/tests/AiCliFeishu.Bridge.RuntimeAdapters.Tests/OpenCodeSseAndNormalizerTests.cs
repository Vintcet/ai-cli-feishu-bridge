using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class OpenCodeSseAndNormalizerTests
{
    private static readonly DateTimeOffset FixedTime =
        DateTimeOffset.Parse("2026-08-06T01:02:03Z");

    [TestMethod]
    public void ParserSupportsChunkingMultilineDataAndTypedPayloads()
    {
        var parser = new OpenCodeSseParser();

        Assert.AreEqual(0, parser.Feed("event: ignored\ndata: {\"sessionID\":\"s0\"}").Count);
        var first = parser.Feed("\n\ndata: {\"type\":\"session.status\",\n");
        var second = parser.Feed("data: \"properties\":{\"sessionID\":\"s1\",\"status\":\"running\"}}\n\n");

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual("ignored", first[0].Type);
        Assert.AreEqual("s0", first[0].Properties.GetProperty("sessionID").GetString());
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual("session.status", second[0].Type);
        Assert.AreEqual("s1", second[0].Properties.GetProperty("sessionID").GetString());
    }

    [TestMethod]
    public void ParserPreservesCrLfWhenChunksSplitAfterCarriageReturn()
    {
        var parser = new OpenCodeSseParser();

        Assert.AreEqual(0, parser.Feed("data: {\"type\":\"session.status\",\r").Count);
        var events = parser.Feed(
            "\ndata: \"properties\":{\"sessionID\":\"s1\",\"status\":\"running\"}}\r\n\r\n");

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("session.status", events[0].Type);
        Assert.AreEqual("s1", events[0].Properties.GetProperty("sessionID").GetString());
    }

    [TestMethod]
    public void MalformedAndTypelessFramesAreIgnored()
    {
        var parser = new OpenCodeSseParser();

        var events = parser.Feed("data: not-json\n\nevent:\ndata: {}\n\n");

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void PermissionAndQuestionEventsUseOnlyStandardPayloads()
    {
        var parser = new OpenCodeSseParser();
        var normalizer = Normalizer();
        var events = ParseAll(parser, """
            data: {"type":"permission.v2.asked","properties":{"id":"permission-1","sessionID":"session-1","action":"shell","input":{"private":"must-not-leak"}}}

            data: {"type":"question.asked","properties":{"id":"question-1","sessionID":"session-1","questions":[{"question":"继续吗？","header":"确认","options":[{"label":"继续","description":"继续执行"}],"multiple":false,"private":"must-not-leak"}]}}

            """.Replace("\r\n", "\n"));

        var approval = normalizer.Normalize(events[0], "trace-sse");
        var input = normalizer.Normalize(events[1], "trace-sse");

        Assert.IsNotNull(approval);
        Assert.AreEqual(RuntimeEventTypes.ApprovalRequested, approval.EventType);
        Assert.AreEqual("permission-1", approval.CorrelationId);
        Assert.IsFalse(approval.Payload.GetRawText().Contains("private", StringComparison.Ordinal));
        Assert.IsNotNull(input);
        Assert.AreEqual(RuntimeEventTypes.InputRequested, input.EventType);
        Assert.AreEqual("question-1", input.CorrelationId);
        Assert.IsFalse(input.Payload.GetRawText().Contains("private", StringComparison.Ordinal));
        Assert.IsTrue(BridgeProtocolValidator.Validate(approval).IsValid);
        Assert.IsTrue(BridgeProtocolValidator.Validate(input).IsValid);
    }

    [TestMethod]
    public void ExternalRepliesAndSessionStatesBecomeStandardEvents()
    {
        var parser = new OpenCodeSseParser();
        var normalizer = Normalizer();
        var frames = ParseAll(parser, """
            data: {"type":"permission.replied","properties":{"sessionID":"session-1","requestID":"permission-1","reply":"reject"}}

            data: {"type":"question.rejected","properties":{"sessionID":"session-1","requestID":"question-1"}}

            data: {"type":"session.status","properties":{"sessionID":"session-1","status":{"type":"running"}}}

            data: {"type":"session.idle","properties":{"sessionID":"session-1"}}

            """.Replace("\r\n", "\n"));
        var events = frames.Select(frame => normalizer.Normalize(frame, "trace-state")).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeEventTypes.ApprovalResolvedExternally,
                RuntimeEventTypes.InputResolvedExternally,
                RuntimeEventTypes.TurnStarted,
                RuntimeEventTypes.TurnCompleted,
            },
            events.Select(runtimeEvent => runtimeEvent!.EventType).ToArray());
        Assert.AreEqual("denied", events[0]!.Payload.GetProperty("outcome").GetString());
        Assert.IsTrue(events.All(runtimeEvent =>
            runtimeEvent is not null && BridgeProtocolValidator.Validate(runtimeEvent).IsValid));
    }

    [TestMethod]
    public void DuplicateFramesRemainSeparateInputsButUnknownOrInvalidEventsAreIgnored()
    {
        var parser = new OpenCodeSseParser();
        var normalizer = Normalizer();
        var frames = ParseAll(parser, """
            data: {"type":"unknown.private","properties":{"sessionID":"session-1"}}

            data: {"type":"permission.asked","properties":{"id":"missing-session"}}

            """.Replace("\r\n", "\n"));

        Assert.IsNull(normalizer.Normalize(frames[0], "trace-invalid"));
        Assert.IsNull(normalizer.Normalize(frames[1], "trace-invalid"));
    }

    [TestMethod]
    public void DuplicateEventIsIgnoredUntilItsFingerprintIsEvicted()
    {
        var normalizer = new OpenCodeEventNormalizer(
            () => Guid.NewGuid().ToString("N"),
            () => FixedTime,
            deduplicationCapacity: 2);
        static OpenCodeRawEvent Permission(string id) => new(
            "permission.asked",
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id,
                sessionID = "session-1",
                action = "shell",
            }));
        var first = Permission("permission-1");

        Assert.IsNotNull(normalizer.Normalize(first, "trace-dedup"));
        Assert.IsNull(normalizer.Normalize(first, "trace-dedup"));
        Assert.IsNotNull(normalizer.Normalize(Permission("permission-2"), "trace-dedup"));
        Assert.IsNotNull(normalizer.Normalize(Permission("permission-3"), "trace-dedup"));
        Assert.IsNotNull(normalizer.Normalize(first, "trace-dedup"));
    }

    [TestMethod]
    public void IdenticalSessionIdleEventsRemainDistinctTurns()
    {
        var normalizer = Normalizer();
        var idle = new OpenCodeRawEvent(
            "session.idle",
            System.Text.Json.JsonSerializer.SerializeToElement(
                new { sessionID = "session-1" }));

        Assert.IsNotNull(normalizer.Normalize(idle, "trace-turn-1"));
        Assert.IsNotNull(normalizer.Normalize(idle, "trace-turn-2"));
    }

    private static OpenCodeEventNormalizer Normalizer() =>
        new(() => "event-fixed", () => FixedTime);

    private static IReadOnlyList<OpenCodeRawEvent> ParseAll(
        OpenCodeSseParser parser,
        string text) =>
        parser.Feed(text).Concat(parser.Complete()).ToArray();
}
