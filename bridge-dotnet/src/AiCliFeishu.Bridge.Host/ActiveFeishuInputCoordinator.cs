using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuInputCoordinator
{
    private const int MaximumAnswerLength = 1_000;
    private const int MaximumAnswerCount = 50;

    private readonly IBridgeActiveInputStateOwner stateOwner;
    private readonly IBridgeRuntimeCommandGateway runtimeCommands;
    private readonly FeishuInteractionCoordinator interactions;
    private readonly IFeishuCardRenderer renderer;
    private readonly IFeishuGateway gateway;
    private readonly IManagedHookResponseSink managedHooks;
    private readonly object interactionSync = new();
    private readonly Dictionary<InputSelectionKey, string[]> selections = [];
    private readonly Dictionary<string, Dictionary<string, FeishuInputCardTarget>> targets =
        new(StringComparer.Ordinal);
    private readonly TimeProvider clock;

    public ActiveFeishuInputCoordinator(
        IBridgeActiveInputStateOwner stateOwner,
        IBridgeRuntimeCommandGateway runtimeCommands,
        FeishuInteractionCoordinator interactions,
        IFeishuCardRenderer renderer,
        IFeishuGateway gateway,
        IManagedHookResponseSink managedHooks,
        TimeProvider? timeProvider = null)
    {
        this.stateOwner = stateOwner;
        this.runtimeCommands = runtimeCommands;
        this.interactions = interactions;
        this.renderer = renderer;
        this.gateway = gateway;
        this.managedHooks = managedHooks;
        clock = timeProvider ?? TimeProvider.System;
    }
}
