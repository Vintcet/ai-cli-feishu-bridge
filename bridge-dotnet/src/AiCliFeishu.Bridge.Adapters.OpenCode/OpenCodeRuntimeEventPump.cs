using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed class OpenCodeRuntimeEventPump(
    IOpenCodeEventSource source,
    OpenCodeEventNormalizer normalizer,
    IRuntimeEventSink eventSink,
    Func<string>? traceIdFactory = null)
{
    private readonly Func<string> nextTraceId =
        traceIdFactory ?? (() => Guid.NewGuid().ToString("N"));

    public async Task RunAsync(
        OpenCodeEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        await foreach (var rawEvent in source.ReadAllAsync(endpoint, cancellationToken))
        {
            var runtimeEvent = normalizer.Normalize(rawEvent, nextTraceId());
            if (runtimeEvent is not null)
            {
                await eventSink.PublishAsync(runtimeEvent, cancellationToken);
            }
        }
    }
}
