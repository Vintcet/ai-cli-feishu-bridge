using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class ClaudeCodeRuntimeAdapter(
    IManagedTerminalDirectory terminals,
    IManagedTerminalTransport terminalTransport,
    IManagedRuntimeLifecycle lifecycle,
    IManagedHookResponseSink hookResponses) : ManagedRuntimeAdapter(
        RuntimeNames.ClaudeCode,
        terminals,
        terminalTransport,
        lifecycle,
        hookResponses);
