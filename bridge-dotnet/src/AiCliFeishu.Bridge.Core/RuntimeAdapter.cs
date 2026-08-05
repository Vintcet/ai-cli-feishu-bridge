using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Core;

public sealed record RuntimeSession(string ExternalId, string? Cwd = null);

public interface IRuntimeAdapter
{
    string Runtime { get; }

    IReadOnlySet<RuntimeCapability> Capabilities { get; }

    bool IsReady(RuntimeSession session);

    Task ExecuteAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default);
}
