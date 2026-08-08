namespace AiCliFeishuControl;

internal enum BridgeHostProductionCutoverState
{
    Completed,
    RolledBack,
    FailedSafe,
    Busy,
    CheckpointRecoveryRequired,
    CheckpointConflict,
    Unavailable,
    Cancelled,
    NotNodeProduction,
}

internal sealed record BridgeHostProductionCutoverResult(
    BridgeHostProductionCutoverState State)
{
    public const string ConfirmationMessage =
        "将停止已认证的 Node 生产 Host，核对生产 Store 已刷盘且唯一 Active Owner 租约已释放，" +
        "随后启动 C# Active Host。失败时只会按持久化检查点证据回退，不会猜测或静默切换。";

    public bool Completed => State is BridgeHostProductionCutoverState.Completed;

    public bool RequiresOwnershipLock => State is not (
        BridgeHostProductionCutoverState.Completed or
        BridgeHostProductionCutoverState.RolledBack or
        BridgeHostProductionCutoverState.NotNodeProduction);

    public string UserMessage => State switch
    {
        BridgeHostProductionCutoverState.Completed =>
            "生产 Host 已安全切换到 C#，持久化检查点已提交。",
        BridgeHostProductionCutoverState.RolledBack =>
            "C# 切换未完成，系统已按持久化证据安全回退到 Node。请检查日志后再重试。",
        BridgeHostProductionCutoverState.Busy =>
            "另一恢复或切换操作正在进行。当前程序未执行新的所有权操作；请关闭其他实例后人工核对。",
        BridgeHostProductionCutoverState.CheckpointRecoveryRequired =>
            "检测到未收敛或遗留的切换检查点。当前程序未猜测 Owner；请退出程序并人工处理检查点恢复。",
        BridgeHostProductionCutoverState.CheckpointConflict =>
            "切换检查点发生并发冲突。当前程序已停止后续所有权操作；请退出程序并人工核对生产 Host。",
        BridgeHostProductionCutoverState.Cancelled =>
            "生产切换已取消。当前程序不会继续执行所有权操作；重新尝试前请核对生产 Host。",
        BridgeHostProductionCutoverState.NotNodeProduction =>
            "当前持久化生产目标不是 Node Production，未执行 Node 到 C# 的切换。",
        BridgeHostProductionCutoverState.FailedSafe =>
            "切换未能安全收敛。当前程序已停止后续所有权操作；请退出程序并人工核对生产 Host 所有权。",
        _ =>
            "无法可靠完成生产切换。当前程序已停止后续所有权操作；请退出程序并人工接管。",
    };
}

internal sealed class BridgeHostProductionCutoverService
{
    private readonly BridgeHostTarget expectedNodeTarget;
    private readonly Func<CancellationToken, ValueTask<BridgeHostStartupRecoveryResult>>
        recoverStartup;
    private readonly Func<BridgeHostTarget> getCurrentTarget;
    private readonly Func<CancellationToken, ValueTask<BridgeStatus?>> readStatus;
    private readonly Func<
        BridgeCutoverHostIdentity,
        CancellationToken,
        ValueTask<BridgeHostPersistentCutoverResult>> executeCutover;
    private readonly Func<CancellationToken, ValueTask> refreshTarget;

    public BridgeHostProductionCutoverService(
        BridgeHostTarget expectedNodeTarget,
        Func<CancellationToken, ValueTask<BridgeHostStartupRecoveryResult>>
            recoverStartup,
        Func<BridgeHostTarget> getCurrentTarget,
        Func<CancellationToken, ValueTask<BridgeStatus?>> readStatus,
        Func<
            BridgeCutoverHostIdentity,
            CancellationToken,
            ValueTask<BridgeHostPersistentCutoverResult>> executeCutover,
        Func<CancellationToken, ValueTask> refreshTarget)
    {
        if (!IsExpectedNodeTarget(expectedNodeTarget))
        {
            throw new ArgumentException(
                "显式生产切换必须绑定固定的 Node Production 目标。",
                nameof(expectedNodeTarget));
        }

        this.expectedNodeTarget = expectedNodeTarget;
        this.recoverStartup = recoverStartup ??
            throw new ArgumentNullException(nameof(recoverStartup));
        this.getCurrentTarget = getCurrentTarget ??
            throw new ArgumentNullException(nameof(getCurrentTarget));
        this.readStatus = readStatus ??
            throw new ArgumentNullException(nameof(readStatus));
        this.executeCutover = executeCutover ??
            throw new ArgumentNullException(nameof(executeCutover));
        this.refreshTarget = refreshTarget ??
            throw new ArgumentNullException(nameof(refreshTarget));
    }

    public async ValueTask<BridgeHostProductionCutoverResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recovery = await recoverStartup(cancellationToken);
            if (recovery.State is BridgeHostStartupRecoveryState.Skipped)
            {
                return new(BridgeHostProductionCutoverState.NotNodeProduction);
            }
            if (!recovery.CanContinue)
            {
                return MapRecoveryFailure(recovery.State);
            }

            var currentTarget = getCurrentTarget();
            if (!IsExpectedNodeTarget(currentTarget) ||
                currentTarget.Port != expectedNodeTarget.Port)
            {
                return new(BridgeHostProductionCutoverState.NotNodeProduction);
            }

            var status = await readStatus(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateExpectedNodeIdentity(status, out var expectedNode))
            {
                return new(BridgeHostProductionCutoverState.Unavailable);
            }

            var persistent = await executeCutover(expectedNode, cancellationToken);
            var result = MapPersistentResult(persistent.State);
            if (result.State is not (
                    BridgeHostProductionCutoverState.Completed or
                    BridgeHostProductionCutoverState.RolledBack))
            {
                return result;
            }

            try
            {
                await refreshTarget(CancellationToken.None);
                var refreshed = getCurrentTarget();
                var targetMatchesResult = result.State switch
                {
                    BridgeHostProductionCutoverState.Completed =>
                        refreshed.Mode is BridgeHostMode.DotNetProduction,
                    BridgeHostProductionCutoverState.RolledBack =>
                        IsExpectedNodeTarget(refreshed),
                    _ => false,
                };
                return targetMatchesResult
                    ? result
                    : new(BridgeHostProductionCutoverState.Unavailable);
            }
            catch
            {
                return new(BridgeHostProductionCutoverState.Unavailable);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(BridgeHostProductionCutoverState.Cancelled);
        }
        catch
        {
            return new(BridgeHostProductionCutoverState.Unavailable);
        }
    }

    private bool TryCreateExpectedNodeIdentity(
        BridgeStatus? status,
        out BridgeCutoverHostIdentity identity)
    {
        identity = null!;
        if (status is not
            {
                Ok: true,
                ProcessId: > 0,
                ActiveOwner: true,
            } ||
            !string.Equals(
                status.HostKind,
                expectedNodeTarget.HostKind,
                StringComparison.Ordinal) ||
            status.ManagementApiVersion != expectedNodeTarget.ManagementApiVersion ||
            !string.Equals(
                status.OwnershipMode,
                expectedNodeTarget.OwnershipMode,
                StringComparison.Ordinal) ||
            !string.Equals(
                status.InstanceName,
                expectedNodeTarget.InstanceName,
                StringComparison.Ordinal))
        {
            return false;
        }

        identity = new(
            status.ProcessId,
            status.HostKind,
            status.ManagementApiVersion,
            status.OwnershipMode,
            status.ActiveOwner,
            status.InstanceName);
        return true;
    }

    private static BridgeHostProductionCutoverResult MapRecoveryFailure(
        BridgeHostStartupRecoveryState state) =>
        state switch
        {
            BridgeHostStartupRecoveryState.Busy =>
                new(BridgeHostProductionCutoverState.Busy),
            BridgeHostStartupRecoveryState.CheckpointRecoveryRequired =>
                new(BridgeHostProductionCutoverState.CheckpointRecoveryRequired),
            BridgeHostStartupRecoveryState.Unavailable =>
                new(BridgeHostProductionCutoverState.Unavailable),
            _ => new(BridgeHostProductionCutoverState.FailedSafe),
        };

    private static BridgeHostProductionCutoverResult MapPersistentResult(
        BridgeHostPersistentCutoverState state) =>
        state switch
        {
            BridgeHostPersistentCutoverState.Completed =>
                new(BridgeHostProductionCutoverState.Completed),
            BridgeHostPersistentCutoverState.RolledBack =>
                new(BridgeHostProductionCutoverState.RolledBack),
            BridgeHostPersistentCutoverState.FailedSafe =>
                new(BridgeHostProductionCutoverState.FailedSafe),
            BridgeHostPersistentCutoverState.Busy =>
                new(BridgeHostProductionCutoverState.Busy),
            BridgeHostPersistentCutoverState.CheckpointRecoveryRequired =>
                new(BridgeHostProductionCutoverState.CheckpointRecoveryRequired),
            BridgeHostPersistentCutoverState.CheckpointConflict =>
                new(BridgeHostProductionCutoverState.CheckpointConflict),
            BridgeHostPersistentCutoverState.Cancelled =>
                new(BridgeHostProductionCutoverState.Cancelled),
            _ => new(BridgeHostProductionCutoverState.Unavailable),
        };

    private static bool IsExpectedNodeTarget(BridgeHostTarget target) =>
        target.Mode is BridgeHostMode.NodeProduction &&
        target.IsProduction &&
        target.UsesNodeRuntime &&
        target.ActiveOwner &&
        string.Equals(target.HostKind, "node", StringComparison.Ordinal) &&
        target.ManagementApiVersion is BridgeHostTarget.CurrentManagementApiVersion &&
        string.Equals(target.OwnershipMode, "active", StringComparison.Ordinal) &&
        string.Equals(target.InstanceName, "production", StringComparison.Ordinal);
}
