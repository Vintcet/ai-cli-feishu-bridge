using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class OpenCodeRuntimeEventPumpTests
{
    [TestMethod]
    public async Task PumpNormalizesDeduplicatesAndPublishesEvents()
    {
        var raw = new OpenCodeRawEvent(
            "permission.asked",
            System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    id = "permission-1",
                    sessionID = "session-1",
                    action = "shell",
                }));
        var source = new SequenceEventSource([raw, raw]);
        var sink = new RecordingRuntimeEventSink();
        var traces = new Queue<string>(["trace-1", "trace-2"]);
        var pump = new OpenCodeRuntimeEventPump(
            source,
            new OpenCodeEventNormalizer(),
            sink,
            traces.Dequeue);

        await pump.RunAsync(new(new Uri("http://127.0.0.1:43210/"), null));

        Assert.AreEqual(1, sink.Events.Count);
        Assert.AreEqual("trace-1", sink.Events[0].TraceId);
    }

    private sealed class SequenceEventSource(
        IReadOnlyList<OpenCodeRawEvent> events) : IOpenCodeEventSource
    {
        public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
            OpenCodeEndpoint endpoint,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            foreach (var rawEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return rawEvent;
                await Task.Yield();
            }
        }
    }
}
