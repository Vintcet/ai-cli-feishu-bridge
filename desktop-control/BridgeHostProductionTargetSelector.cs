namespace AiCliFeishuControl;

internal enum BridgeHostProductionTargetSelectionFailure
{
    RecoveryRequired,
    InvalidCheckpoint,
    Unavailable,
    ObservationChanged,
    UnsupportedIdentity,
}

internal sealed class BridgeHostProductionTargetSelectionException(
    BridgeHostProductionTargetSelectionFailure failure,
    string message) : InvalidOperationException(message)
{
    public BridgeHostProductionTargetSelectionFailure Failure { get; } = failure;
}

internal sealed class BridgeHostProductionTargetSelector
{
    private readonly string dataDirectory;
    private readonly int productionPort;
    private readonly Func<CancellationToken, ValueTask<BridgeHostCutoverCheckpointReadResult>>
        readCheckpoint;
    private readonly Func<string, BridgeHostCutoverCheckpointRecoveryState>
        inspectRecovery;

    public BridgeHostProductionTargetSelector(
        string dataDirectory,
        int productionPort)
        : this(
            dataDirectory,
            productionPort,
            new BridgeHostCutoverCheckpointStore(dataDirectory).ReadAsync,
            BridgeHostCutoverCheckpointRecovery.Inspect)
    {
    }

    internal BridgeHostProductionTargetSelector(
        string dataDirectory,
        int productionPort,
        Func<CancellationToken, ValueTask<BridgeHostCutoverCheckpointReadResult>>
            readCheckpoint,
        Func<string, BridgeHostCutoverCheckpointRecoveryState> inspectRecovery)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "生产 Host 目标选择器的数据目录不能为空。",
                nameof(dataDirectory));
        }
        if (productionPort is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(productionPort));
        }

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.productionPort = productionPort;
        this.readCheckpoint = readCheckpoint ??
            throw new ArgumentNullException(nameof(readCheckpoint));
        this.inspectRecovery = inspectRecovery ??
            throw new ArgumentNullException(nameof(inspectRecovery));
    }

    public async ValueTask<BridgeHostTarget> SelectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireCleanRecoveryState(inspectRecovery(dataDirectory));
        var before = await readCheckpoint(cancellationToken);
        RequireCleanRecoveryState(inspectRecovery(dataDirectory));
        var after = await readCheckpoint(cancellationToken);
        if (!IsStable(before, after))
        {
            throw Failure(
                BridgeHostProductionTargetSelectionFailure.ObservationChanged,
                "生产 Host 切换检查点在读取期间发生变化，已拒绝猜测当前 Owner。");
        }

        return after.State switch
        {
            BridgeHostCutoverCheckpointReadState.Missing =>
                BridgeHostTarget.NodeProduction(productionPort),
            BridgeHostCutoverCheckpointReadState.Present =>
                SelectPresent(after.Checkpoint),
            BridgeHostCutoverCheckpointReadState.Invalid => throw Failure(
                BridgeHostProductionTargetSelectionFailure.InvalidCheckpoint,
                "生产 Host 切换检查点无效，需要人工接管。"),
            _ => throw Failure(
                BridgeHostProductionTargetSelectionFailure.Unavailable,
                "无法可靠读取生产 Host 切换检查点，需要人工接管。"),
        };
    }

    private BridgeHostTarget SelectPresent(BridgeHostCutoverCheckpoint? checkpoint)
    {
        if (checkpoint is null ||
            !string.Equals(
                checkpoint.ExpectedNode.InstanceName,
                "production",
                StringComparison.Ordinal) ||
            !string.Equals(
                checkpoint.ExpectedDotNetInstanceName,
                BridgeHostTarget.DotNetProductionInstanceName,
                StringComparison.Ordinal))
        {
            throw Failure(
                BridgeHostProductionTargetSelectionFailure.UnsupportedIdentity,
                "生产 Host 切换检查点未绑定受支持的生产实例，需要人工接管。");
        }

        return checkpoint.Stage switch
        {
            BridgeHostCutoverStage.Completed => BridgeHostTarget.DotNetProduction(
                productionPort,
                checkpoint.ExpectedDotNetInstanceName),
            BridgeHostCutoverStage.RolledBack =>
                BridgeHostTarget.NodeProduction(productionPort),
            _ => throw Failure(
                BridgeHostProductionTargetSelectionFailure.RecoveryRequired,
                "生产 Host 切换尚未收敛，必须先执行隔离恢复。"),
        };
    }

    private static bool IsStable(
        BridgeHostCutoverCheckpointReadResult before,
        BridgeHostCutoverCheckpointReadResult after) =>
        BridgeHostCutoverCheckpointStore.HasSameFileVersion(before, after) &&
        before.Checkpoint == after.Checkpoint;

    private static void RequireCleanRecoveryState(
        BridgeHostCutoverCheckpointRecoveryState state)
    {
        if (state is BridgeHostCutoverCheckpointRecoveryState.Clean)
        {
            return;
        }
        throw Failure(
            state is BridgeHostCutoverCheckpointRecoveryState.Unavailable
                ? BridgeHostProductionTargetSelectionFailure.Unavailable
                : BridgeHostProductionTargetSelectionFailure.RecoveryRequired,
            state is BridgeHostCutoverCheckpointRecoveryState.Unavailable
                ? "无法检查生产 Host 切换恢复状态，需要人工接管。"
                : "检测到未完成的切换检查点文件恢复工作，需要人工接管。");
    }

    private static BridgeHostProductionTargetSelectionException Failure(
        BridgeHostProductionTargetSelectionFailure failure,
        string message) =>
        new(failure, message);
}

internal sealed class BridgeHostTargetState
{
    private readonly BridgeHostTarget configuredTarget;
    private readonly Func<CancellationToken, ValueTask<BridgeHostTarget>>?
        selectProductionTarget;
    private BridgeHostTarget current;

    public BridgeHostTargetState(
        BridgeHostTarget configuredTarget,
        Func<CancellationToken, ValueTask<BridgeHostTarget>>?
            selectProductionTarget = null)
    {
        this.configuredTarget = configuredTarget ??
            throw new ArgumentNullException(nameof(configuredTarget));
        if (configuredTarget.IsProduction && selectProductionTarget is null)
        {
            throw new ArgumentNullException(nameof(selectProductionTarget));
        }
        this.selectProductionTarget = selectProductionTarget;
        current = configuredTarget;
    }

    public BridgeHostTarget Current => Volatile.Read(ref current);

    public async ValueTask<BridgeHostTarget> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (!configuredTarget.IsProduction)
        {
            return Current;
        }

        var selected = await selectProductionTarget!(cancellationToken);
        if (!IsBoundProductionTarget(selected) ||
            selected.Port != configuredTarget.Port)
        {
            throw new InvalidOperationException(
                "持久化生产 Host 目标超出已绑定的本机生产端点。");
        }
        Volatile.Write(ref current, selected);
        return selected;
    }

    private static bool IsBoundProductionTarget(BridgeHostTarget target) =>
        target.Mode switch
        {
            BridgeHostMode.NodeProduction =>
                target.IsProduction &&
                target.UsesNodeRuntime &&
                target.ActiveOwner &&
                string.Equals(target.HostKind, "node", StringComparison.Ordinal) &&
                string.Equals(target.OwnershipMode, "active", StringComparison.Ordinal) &&
                string.Equals(target.InstanceName, "production", StringComparison.Ordinal),
            BridgeHostMode.DotNetProduction =>
                target.IsProduction &&
                !target.UsesNodeRuntime &&
                target.ActiveOwner &&
                string.Equals(target.HostKind, "dotnet", StringComparison.Ordinal) &&
                string.Equals(target.OwnershipMode, "active", StringComparison.Ordinal) &&
                string.Equals(
                    target.InstanceName,
                    BridgeHostTarget.DotNetProductionInstanceName,
                    StringComparison.Ordinal),
            _ => false,
        };
}
