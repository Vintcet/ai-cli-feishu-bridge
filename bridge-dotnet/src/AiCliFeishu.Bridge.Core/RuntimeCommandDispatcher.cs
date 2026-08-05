using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Core;

public sealed class RuntimeCommandDispatcher(RuntimeAdapterRegistry registry)
{
    public bool IsReady(string runtime, RuntimeSession session)
    {
        return registry.ForRuntime(runtime).IsReady(session);
    }

    public async Task DispatchAsync(
        RuntimeCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        var validation = BridgeProtocolValidator.Validate(command);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"运行时命令不符合 Bridge Protocol：{string.Join("；", validation.Errors)}");
        }

        var adapter = registry.RequireCapabilities(
            command.Runtime,
            RuntimeCommandCapabilities.RequiredBy(command));
        await adapter.ExecuteAsync(command, cancellationToken);
    }
}
