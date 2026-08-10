using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal interface IBridgeActiveApprovalNotifier
{
    Task NotifyPendingAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SynchronizeAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SynchronizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

internal interface IBridgeActiveInputNotifier
{
    Task NotifyPendingInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SynchronizeInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SynchronizeInputSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ActiveFeishuApprovalNotificationCoordinator(
    IBridgeActiveApprovalStateOwner stateOwner,
    IBridgeProductionStoreOwner storeOwner,
    IFeishuGateway gateway,
    IFeishuCardRenderer renderer,
    FeishuInteractionCoordinator interactions,
    IBridgeActiveSessionGroupCoordinator sessionGroups,
    IBridgeActiveInputStateOwner? inputStateOwner = null,
    Func<IManagedHookResponseSink>? managedHooks = null,
    ActiveFeishuApprovalCoordinator? approvals = null)
    : IBridgeActiveApprovalNotifier,
      IBridgeActiveInputNotifier,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth,
      IBridgeBackgroundSubsystem,
      IDisposable
{
    private static readonly TimeSpan SynchronizationInterval = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly SemaphoreSlim synchronizationGate = new(1, 1);
    private readonly SemaphoreSlim inputSynchronizationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? synchronizationLoop;
    private bool started;
    private bool disposed;
    private int synchronizationRuns;
    private int synchronizationFailures;
    private int inputSynchronizationFailures;

    public string Name => "active-feishu-approval-notifications";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                return new(
                    Name,
                    started ? "ready" : "starting",
                    $"runs={synchronizationRuns} failed={synchronizationFailures} " +
                    $"inputFailed={inputSynchronizationFailures}");
            }
        }
    }

    public Task? Completion
    {
        get
        {
            lock (sync)
            {
                return synchronizationLoop;
            }
        }
    }


}
