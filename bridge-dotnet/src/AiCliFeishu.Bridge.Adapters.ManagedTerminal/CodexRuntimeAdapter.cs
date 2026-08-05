using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class CodexRuntimeAdapter(
    IManagedTerminalDirectory terminals,
    IManagedTerminalTransport terminalTransport,
    IManagedRuntimeLifecycle lifecycle,
    IManagedHookResponseSink hookResponses) : ManagedRuntimeAdapter(
        RuntimeNames.Codex,
        terminals,
        terminalTransport,
        lifecycle,
        hookResponses);
