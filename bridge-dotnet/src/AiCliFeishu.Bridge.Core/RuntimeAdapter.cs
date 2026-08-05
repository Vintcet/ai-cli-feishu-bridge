using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Core;

public sealed record RuntimeSession(string ExternalId, string? Cwd = null);

public sealed record RuntimeCommandContext(
    string CommandId,
    string TraceId,
    string? CorrelationId)
{
    public static RuntimeCommandContext From(RuntimeCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new(command.CommandId, command.TraceId, command.CorrelationId);
    }
}

public interface IRuntimeAdapter
{
    string Runtime { get; }

    IReadOnlySet<RuntimeCapability> Capabilities { get; }

    bool IsReady(RuntimeSession session);

    Task ExecuteAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeEventSink
{
    Task PublishAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default);
}
