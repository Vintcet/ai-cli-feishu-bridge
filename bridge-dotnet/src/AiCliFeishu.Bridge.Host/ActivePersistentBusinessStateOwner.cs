using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActivePersistentBusinessStateOwner(
    BridgeHostOptions options,
    IBridgeProductionStoreOwner storeOwner,
    TimeProvider? timeProvider = null)
    : IBridgePersistentBusinessStateOwner,
      IBridgeControlBusinessStateSource,
      IBridgeActiveRuntimeStateSink,
      IBridgeActiveSessionAliasStateOwner,
      IBridgeActiveSessionGroupStateOwner,
      IBridgeActiveApprovalStateOwner,
      IBridgeActiveInputStateOwner,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly HashSet<string> inputClaims = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private BridgeBusinessStateSnapshot snapshot =
        BridgeBusinessStateSnapshot.NotInitialized;

    public string Name => "persistent-business-state-owner";

    public BridgeBusinessStateSnapshot Snapshot => Volatile.Read(ref snapshot);

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = Snapshot;
            return current.Initialized
                ? new(
                    Name,
                    "ready",
                    $"persistent sessions={current.Sessions.Sessions.Count} " +
                    $"approvals={current.Approvals.Requests.Count} " +
                    $"inputs={current.Inputs.Requests.Count}")
                : new(Name, "failed", $"source={current.SourceStatus}");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.Initialized)
            {
                return;
            }
            var store = await storeOwner.ReadAsync(cancellationToken);
            var core = BridgeStoreCoreProjection.Project(store);
            var inputs = BridgeStoreCoreProjection.ProjectInputs(store);
            var observedAt = clock.GetUtcNow();
            var recovered = ApprovalStateMachine.RecoverPending(
                core.Approvals,
                observedAt);
            var sessions = RecoverApprovalSessions(
                core.Sessions,
                core.Approvals,
                observedAt);
            sessions = BridgeStoreRetention.PruneEndedSessions(sessions, observedAt);
            var retainedApprovals = ApprovalRetention.Prune(
                recovered.State,
                observedAt,
                RetentionPolicy.Default);
            var initialized = new BridgeBusinessStateSnapshot(
                true,
                "production",
                0,
                0,
                sessions,
                retainedApprovals,
                inputs);

            var sessionsChanged = !ReferenceEquals(sessions, core.Sessions);
            var approvalsChanged = !ReferenceEquals(retainedApprovals, recovered.State);
            if (recovered.Value > 0 || sessionsChanged || approvalsChanged)
            {
                await PersistAsync(initialized, cancellationToken);
            }
            Volatile.Write(ref snapshot, initialized);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Active business state is the authoritative in-memory projection. It is
        // advanced only after the production Store write succeeds; a control API
        // refresh must not re-read the files and overwrite newer runtime state.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }


    private async Task PersistAsync(
        BridgeBusinessStateSnapshot business,
        CancellationToken cancellationToken,
        BridgeStoreSessionExtensionPatch? sessionExtensionPatch = null,
        BridgeStoreApprovalExtensionPatch? approvalExtensionPatch = null)
    {
        await storeOwner.UpdateAsync(
            store => BridgeStoreBusinessStateMerger.Merge(
                store,
                business.Sessions,
                business.Approvals,
                business.Inputs,
                sessionExtensionPatch,
                approvalExtensionPatch),
            cancellationToken);
    }

    private static DateTimeOffset Latest(params DateTimeOffset[] values) =>
        values.Max();

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "持久化业务状态 Owner 只能用于 Active Host。");
        }
    }

    private static SessionDirectoryState RecoverApprovalSessions(
        SessionDirectoryState sessions,
        ApprovalRegistryState loadedApprovals,
        DateTimeOffset observedAt)
    {
        foreach (var loaded in loadedApprovals.Requests.Values.Where(item =>
                     item.Status == ApprovalStatuses.Pending))
        {
            if (!sessions.Sessions.TryGetValue(loaded.SessionId, out var session) ||
                session.Status != SessionStatuses.PendingApproval)
            {
                continue;
            }
            var occurredAt = observedAt >= session.LastSeenAt
                ? observedAt
                : session.LastSeenAt;
            sessions = SessionStateMachine.Transition(
                sessions,
                session.SessionId,
                SessionStatuses.LocalApproval,
                occurredAt);
        }
        return sessions;
    }

    private BridgeBusinessStateSnapshot RequireInitialized() =>
        Snapshot.Initialized
            ? Snapshot
            : throw new InvalidOperationException(
                $"业务状态所有者尚未从生产 Store 初始化：{Snapshot.SourceStatus}。");
}
