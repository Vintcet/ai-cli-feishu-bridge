using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Core;

public enum RuntimeCapability
{
    PromptSend,
    PromptQueue,
    ApprovalResolve,
    InputResolve,
    SessionLaunch,
    SessionResume,
    SessionStop,
    ActivityStream,
}

public static class RuntimeCommandCapabilities
{
    public static IReadOnlyList<RuntimeCapability> RequiredBy(
        RuntimeCommandEnvelope command)
    {
        return command.CommandType switch
        {
            RuntimeCommandTypes.PromptSend when IsQueue(command) =>
                [RuntimeCapability.PromptSend, RuntimeCapability.PromptQueue],
            RuntimeCommandTypes.PromptSend => [RuntimeCapability.PromptSend],
            RuntimeCommandTypes.ApprovalResolve => [RuntimeCapability.ApprovalResolve],
            RuntimeCommandTypes.InputResolve => [RuntimeCapability.InputResolve],
            RuntimeCommandTypes.SessionLaunch => [RuntimeCapability.SessionLaunch],
            RuntimeCommandTypes.SessionResume => [RuntimeCapability.SessionResume],
            RuntimeCommandTypes.SessionStop => [RuntimeCapability.SessionStop],
            _ => throw new InvalidOperationException(
                $"命令 {command.CommandType} 没有对应的运行时能力。"),
        };
    }

    private static bool IsQueue(RuntimeCommandEnvelope command)
    {
        return command.Payload.TryGetProperty("mode", out var mode) &&
            string.Equals(mode.GetString(), "queue", StringComparison.Ordinal);
    }
}
