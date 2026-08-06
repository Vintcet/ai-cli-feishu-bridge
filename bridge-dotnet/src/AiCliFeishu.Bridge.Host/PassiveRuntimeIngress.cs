using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

public interface IBridgeRuntimeIngressAssembly
{
    BridgeRuntimeIngressSnapshot Validate();

    BridgeRuntimeIngressSnapshot Snapshot();
}

public sealed record BridgeRuntimeIngressSnapshot(
    string Mode,
    IReadOnlyList<string> Components,
    bool ManagedHookHttpEnabled,
    bool OpenCodeEventStreamEnabled);

/// <summary>
/// Runtime 入站 Adapter 的被动组合根。它验证 Hook 和 SSE 的原生事件都能经过
/// 各自 Normalizer 进入唯一标准事件 Sink，但不会开放 Hook HTTP 或订阅 SSE。
/// </summary>
public sealed class BridgeRuntimeIngressAssembly(
    ManagedRuntimeHookNormalizer managedHookNormalizer,
    ManagedRuntimeHookBridge managedHookBridge,
    IOpenCodeEventSource openCodeEventSource,
    OpenCodeEventNormalizer openCodeEventNormalizer,
    OpenCodeRuntimeEventPump openCodeEventPump,
    IRuntimeEventSink eventSink,
    BridgeHostOptions options) : IBridgeRuntimeIngressAssembly
{
    private static readonly string[] componentNames =
    [
        "managed-hook-bridge",
        "managed-hook-normalizer",
        "opencode-event-normalizer",
        "opencode-event-pump",
        "opencode-event-source",
        "runtime-event-sink",
    ];

    public BridgeRuntimeIngressSnapshot Validate()
    {
        _ = managedHookNormalizer;
        _ = managedHookBridge;
        _ = openCodeEventNormalizer;
        _ = openCodeEventPump;
        _ = eventSink;
        if (options.OwnershipMode is BridgeOwnershipMode.Passive &&
            openCodeEventSource is not PassiveOpenCodeEventSource)
        {
            throw new InvalidOperationException(
                "Passive Host 的 Runtime 入站链必须使用无网络 OpenCode 事件源。");
        }
        return Snapshot();
    }

    public BridgeRuntimeIngressSnapshot Snapshot() => new(
        options.OwnershipMode is BridgeOwnershipMode.Active ? "active" : "passive",
        componentNames,
        ManagedHookHttpEnabled: false,
        OpenCodeEventStreamEnabled: false);
}
