namespace AiCliFeishuControl;

internal enum BridgeHostStartupRecoveryState
{
    Skipped,
    NotRequired,
    NoActionRequired,
    Recovered,
    ManualIntervention,
    Busy,
    CheckpointRecoveryRequired,
    Unavailable,
    UnsafeStoreHandoff,
    FailedSafe,
}

internal sealed record BridgeHostStartupRecoveryResult(
    BridgeHostStartupRecoveryState State,
    BridgeHostRecoveryReason Reason = BridgeHostRecoveryReason.None)
{
    public bool CanContinue => State is
        BridgeHostStartupRecoveryState.Skipped or
        BridgeHostStartupRecoveryState.NotRequired or
        BridgeHostStartupRecoveryState.NoActionRequired or
        BridgeHostStartupRecoveryState.Recovered;

    public string UserMessage => State switch
    {
        BridgeHostStartupRecoveryState.Skipped =>
            "当前为隔离 Shadow Host，不执行生产 Owner 恢复。",
        BridgeHostStartupRecoveryState.NotRequired =>
            "未发现生产 Host 切换检查点，无需恢复。",
        BridgeHostStartupRecoveryState.NoActionRequired =>
            "生产 Host 所有权与持久化检查点一致，无需恢复。",
        BridgeHostStartupRecoveryState.Recovered =>
            "已按持久化检查点安全恢复生产 Host。",
        BridgeHostStartupRecoveryState.Busy =>
            "另一恢复或切换操作正在进行。为避免双 Owner，当前程序未执行任何新操作；请关闭其他实例后人工重试。",
        BridgeHostStartupRecoveryState.CheckpointRecoveryRequired =>
            "检测到切换检查点遗留文件。为避免猜测所有权，当前程序未启动或停止任何 Host；请退出程序后人工处理检查点恢复。",
        BridgeHostStartupRecoveryState.UnsafeStoreHandoff =>
            "生产 Store 尚不满足安全交接条件。当前程序未继续切换，请人工检查 Owner 租约与 Store 状态。",
        BridgeHostStartupRecoveryState.FailedSafe =>
            "自动恢复未能安全收敛，系统已停止继续操作。请保持程序退出并人工核对生产 Host 所有权。",
        BridgeHostStartupRecoveryState.Unavailable =>
            "无法可靠读取或执行生产 Host 恢复。为避免双 Owner，当前程序未继续操作，请人工接管。",
        _ =>
            "生产 Host 所有权状态不确定。为避免双 Owner，当前程序未启动或停止任何 Host，请人工接管。",
    };
}

internal sealed class BridgeHostStartupRecovery
{
    private readonly string dataDirectory;
    private readonly Func<CancellationToken, ValueTask<BridgeHostCutoverCheckpointReadResult>>
        readCheckpoint;
    private readonly Func<string, BridgeHostCutoverCheckpointRecoveryState>
        inspectCheckpointRecovery;
    private readonly Func<CancellationToken, ValueTask<BridgeHostRecoveryExecutionResult>>
        executeRecovery;
    private readonly Func<CancellationToken, ValueTask> refreshTarget;

    public BridgeHostStartupRecovery(
        string dataDirectory,
        Func<CancellationToken, ValueTask<BridgeHostRecoveryExecutionResult>>
            executeRecovery,
        Func<CancellationToken, ValueTask> refreshTarget)
        : this(
            dataDirectory,
            new BridgeHostCutoverCheckpointStore(dataDirectory).ReadAsync,
            BridgeHostCutoverCheckpointRecovery.Inspect,
            executeRecovery,
            refreshTarget)
    {
    }

    internal BridgeHostStartupRecovery(
        string dataDirectory,
        Func<CancellationToken, ValueTask<BridgeHostCutoverCheckpointReadResult>>
            readCheckpoint,
        Func<string, BridgeHostCutoverCheckpointRecoveryState>
            inspectCheckpointRecovery,
        Func<CancellationToken, ValueTask<BridgeHostRecoveryExecutionResult>>
            executeRecovery,
        Func<CancellationToken, ValueTask> refreshTarget)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "启动恢复的数据目录不能为空。",
                nameof(dataDirectory));
        }
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.readCheckpoint = readCheckpoint ??
            throw new ArgumentNullException(nameof(readCheckpoint));
        this.inspectCheckpointRecovery = inspectCheckpointRecovery ??
            throw new ArgumentNullException(nameof(inspectCheckpointRecovery));
        this.executeRecovery = executeRecovery ??
            throw new ArgumentNullException(nameof(executeRecovery));
        this.refreshTarget = refreshTarget ??
            throw new ArgumentNullException(nameof(refreshTarget));
    }

    public async ValueTask<BridgeHostStartupRecoveryResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recoveryState = inspectCheckpointRecovery(dataDirectory);
            if (recoveryState is not BridgeHostCutoverCheckpointRecoveryState.Clean)
            {
                return RecoveryStateFailure(recoveryState);
            }

            var before = await readCheckpoint(cancellationToken);
            recoveryState = inspectCheckpointRecovery(dataDirectory);
            if (recoveryState is not BridgeHostCutoverCheckpointRecoveryState.Clean)
            {
                return RecoveryStateFailure(recoveryState);
            }
            var after = await readCheckpoint(cancellationToken);
            if (!IsStable(before, after))
            {
                return Manual(BridgeHostRecoveryReason.CheckpointChanged);
            }

            if (after.State is BridgeHostCutoverCheckpointReadState.Missing)
            {
                return await RefreshAndReturnAsync(
                    new(BridgeHostStartupRecoveryState.NotRequired),
                    cancellationToken);
            }
            if (after.State is BridgeHostCutoverCheckpointReadState.Unavailable)
            {
                return new(BridgeHostStartupRecoveryState.Unavailable);
            }
            if (after.State is BridgeHostCutoverCheckpointReadState.Invalid ||
                after.Checkpoint is null ||
                !BridgeHostCutoverCheckpointValidator.IsValid(after.Checkpoint))
            {
                return Manual(BridgeHostRecoveryReason.InvalidCheckpoint);
            }

            var execution = await executeRecovery(cancellationToken);
            var result = Map(execution);
            return result.CanContinue
                ? await RefreshAndReturnAsync(result, cancellationToken)
                : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(BridgeHostStartupRecoveryState.Unavailable);
        }
    }

    private async ValueTask<BridgeHostStartupRecoveryResult> RefreshAndReturnAsync(
        BridgeHostStartupRecoveryResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await refreshTarget(cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }
    }

    private static BridgeHostStartupRecoveryResult Map(
        BridgeHostRecoveryExecutionResult execution) =>
        execution.State switch
        {
            BridgeHostRecoveryExecutionState.NoActionRequired =>
                new(BridgeHostStartupRecoveryState.NoActionRequired),
            BridgeHostRecoveryExecutionState.Recovered =>
                new(BridgeHostStartupRecoveryState.Recovered),
            BridgeHostRecoveryExecutionState.ManualIntervention =>
                Manual(execution.Plan?.Reason ?? BridgeHostRecoveryReason.InvalidCheckpoint),
            BridgeHostRecoveryExecutionState.Busy =>
                new(BridgeHostStartupRecoveryState.Busy),
            BridgeHostRecoveryExecutionState.CheckpointRecoveryRequired =>
                new(BridgeHostStartupRecoveryState.CheckpointRecoveryRequired),
            BridgeHostRecoveryExecutionState.UnsafeStoreHandoff =>
                new(BridgeHostStartupRecoveryState.UnsafeStoreHandoff),
            BridgeHostRecoveryExecutionState.FailedSafe =>
                new(
                    BridgeHostStartupRecoveryState.FailedSafe,
                    execution.Plan?.Reason ?? BridgeHostRecoveryReason.None),
            _ => new(BridgeHostStartupRecoveryState.Unavailable),
        };

    private static BridgeHostStartupRecoveryResult RecoveryStateFailure(
        BridgeHostCutoverCheckpointRecoveryState state) =>
        state is BridgeHostCutoverCheckpointRecoveryState.Unavailable
            ? new(BridgeHostStartupRecoveryState.Unavailable)
            : new(BridgeHostStartupRecoveryState.CheckpointRecoveryRequired);

    private static BridgeHostStartupRecoveryResult Manual(
        BridgeHostRecoveryReason reason) =>
        new(BridgeHostStartupRecoveryState.ManualIntervention, reason);

    private static bool IsStable(
        BridgeHostCutoverCheckpointReadResult before,
        BridgeHostCutoverCheckpointReadResult after) =>
        BridgeHostCutoverCheckpointStore.HasSameFileVersion(before, after) &&
        before.Checkpoint == after.Checkpoint;
}
