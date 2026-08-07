namespace AiCliFeishuControl;

internal delegate ValueTask BridgeHostProcessStartedCallback(
    int processId,
    CancellationToken cancellationToken);

internal interface IBridgeHostPersistentCutoverOperations :
    IBridgeHostRecoveryOperations
{
    ValueTask<int> StartDotNetActiveAndBindAsync(
        string instanceName,
        BridgeHostProcessStartedCallback processStarted,
        CancellationToken cancellationToken);

    ValueTask<int> StartNodeActiveAndBindAsync(
        BridgeHostProcessStartedCallback processStarted,
        CancellationToken cancellationToken);
}

internal enum BridgeHostPersistentCutoverState
{
    Completed,
    RolledBack,
    FailedSafe,
    Cancelled,
    Busy,
    CheckpointRecoveryRequired,
    CheckpointConflict,
    Unavailable,
}

internal sealed record BridgeHostPersistentCutoverResult(
    BridgeHostPersistentCutoverState State,
    BridgeHostCutoverSnapshot? DurableSnapshot = null)
{
    public bool Completed => State is BridgeHostPersistentCutoverState.Completed;

    public bool RolledBack => State is BridgeHostPersistentCutoverState.RolledBack;
}

internal sealed class BridgeHostPersistentCutoverCoordinator
{
    internal delegate ValueTask<BridgeHostCutoverCheckpointWriteResult>
        WriteCheckpointAsync(
            BridgeHostCutoverCheckpointWriter writer,
            BridgeHostCutoverCheckpoint checkpoint,
            CancellationToken cancellationToken);

    private readonly string dataDirectory;
    private readonly BridgeHostCutoverCheckpointStore checkpointStore;
    private readonly IBridgeHostPersistentCutoverOperations operations;
    private readonly TimeProvider timeProvider;
    private readonly Func<string> createOperationId;
    private readonly WriteCheckpointAsync writeCheckpoint;

    public BridgeHostPersistentCutoverCoordinator(
        string dataDirectory,
        IBridgeHostPersistentCutoverOperations operations)
        : this(
            dataDirectory,
            operations,
            TimeProvider.System,
            static () => Guid.NewGuid().ToString("N"),
            static (writer, checkpoint, cancellationToken) =>
                writer.TryWriteAsync(checkpoint, cancellationToken))
    {
    }

    internal BridgeHostPersistentCutoverCoordinator(
        string dataDirectory,
        IBridgeHostPersistentCutoverOperations operations,
        TimeProvider timeProvider,
        Func<string> createOperationId,
        WriteCheckpointAsync writeCheckpoint)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "持久化切换协调器的数据目录不能为空。",
                nameof(dataDirectory));
        }

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        checkpointStore = new BridgeHostCutoverCheckpointStore(this.dataDirectory);
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.createOperationId = createOperationId ??
            throw new ArgumentNullException(nameof(createOperationId));
        this.writeCheckpoint = writeCheckpoint ??
            throw new ArgumentNullException(nameof(writeCheckpoint));
    }

    public async ValueTask<BridgeHostPersistentCutoverResult> RunAsync(
        BridgeCutoverHostIdentity expectedNode,
        string dotNetInstanceName,
        CancellationToken cancellationToken = default)
    {
        BridgeHostCutoverTransaction transaction;
        try
        {
            transaction = BridgeHostCutoverTransaction.Create(
                expectedNode,
                dotNetInstanceName);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(BridgeHostPersistentCutoverState.Cancelled);
        }

        var operationId = createOperationId();
        if (!BridgeHostCutoverCheckpointValidator.IsValidOperationId(operationId))
        {
            throw new InvalidDataException("持久化切换 operationId 无效。");
        }

        try
        {
            var acquisition = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
                dataDirectory,
                operationId,
                cancellationToken);
            if (acquisition.State is not
                    BridgeHostCutoverCheckpointWriterAcquireState.Acquired ||
                acquisition.Writer is null)
            {
                return AcquisitionFailure(acquisition.State);
            }

            using var writer = acquisition.Writer;
            var current = await checkpointStore.ReadAsync(cancellationToken);
            var baseline = GetTimestampBaseline(current, out var blockedState);
            if (blockedState is not null)
            {
                return new(blockedState.Value);
            }
            var context = new PersistenceContext(
                transaction,
                writer,
                operationId,
                expectedNode,
                dotNetInstanceName,
                baseline,
                timeProvider,
                writeCheckpoint);
            await context.PersistCurrentAsync(cancellationToken);

            // Planned 一旦发布，后续安全序列不再接受调用方取消。
            return await RunSafetySequenceAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(BridgeHostPersistentCutoverState.Cancelled);
        }
        catch (CheckpointPersistenceException error)
        {
            return new(error.State);
        }
    }

    private async ValueTask<BridgeHostPersistentCutoverResult> RunSafetySequenceAsync(
        PersistenceContext context)
    {
        var dotNetProcessId = 0;
        try
        {
            await context.ApplyAndPersistAsync(
                new NodeStopRequestedEvent(context.ExpectedNode));
            await operations.RequestNodeStopAsync(
                context.ExpectedNode,
                CancellationToken.None);

            await operations.VerifyNodeOfflineAsync(
                context.ExpectedNode.ProcessId,
                CancellationToken.None);
            await context.ApplyAndPersistAsync(
                new NodeOfflineVerifiedEvent(context.ExpectedNode.ProcessId));

            var handoff = await operations.InspectStoreHandoffAsync(
                CancellationToken.None);
            await context.ApplyAndPersistAsync(
                new StoreHandoffVerifiedEvent(handoff));
            if (context.Snapshot.Stage is BridgeHostCutoverStage.RollbackRequired)
            {
                return await RollBackAsync(context, dotNetProcessId);
            }
            if (context.Snapshot.IsTerminal)
            {
                return TerminalResult(context);
            }

            var launchCallbackInvoked = false;
            async ValueTask BindDotNetProcessAsync(
                int processId,
                CancellationToken cancellationToken)
            {
                if (launchCallbackInvoked || processId <= 0)
                {
                    throw OperationFailure(
                        BridgeCutoverFailureReason.OwnershipUncertain,
                        "C# Active Host 启动 PID 无效或回调被重复调用。");
                }
                await context.ApplyAndPersistAsync(
                    new DotNetStartRequestedEvent(processId));
                dotNetProcessId = processId;
                launchCallbackInvoked = true;
            }

            var returnedDotNetProcessId =
                await operations.StartDotNetActiveAndBindAsync(
                    context.DotNetInstanceName,
                    BindDotNetProcessAsync,
                    CancellationToken.None);
            if (!launchCallbackInvoked ||
                returnedDotNetProcessId != dotNetProcessId ||
                dotNetProcessId <= 0)
            {
                throw OperationFailure(
                    BridgeCutoverFailureReason.OwnershipUncertain,
                    "C# Active Host 启动 PID 未与持久化检查点绑定。");
            }

            var dotNetIdentity = await operations.VerifyDotNetActiveAsync(
                dotNetProcessId,
                context.DotNetInstanceName,
                CancellationToken.None);
            await context.ApplyAndPersistAsync(
                new DotNetActiveVerifiedEvent(dotNetIdentity));
            if (context.Snapshot.Stage is BridgeHostCutoverStage.RollbackRequired)
            {
                return await RollBackAsync(context, dotNetProcessId);
            }

            await context.ApplyAndPersistAsync(new CutoverCompletedEvent());
            return TerminalResult(context);
        }
        catch (CheckpointPersistenceException error)
        {
            return PersistenceFailure(error, context);
        }
        catch (BridgeHostCutoverOperationException error)
        {
            if (context.Snapshot.IsTerminal)
            {
                return TerminalResult(context);
            }
            return await FailAsync(
                context,
                FailureForOperationException(
                    context.Snapshot.Stage,
                    error.Reason,
                    dotNetProcessId),
                dotNetProcessId);
        }
        catch
        {
            if (context.Snapshot.IsTerminal)
            {
                return TerminalResult(context);
            }
            return await FailAsync(
                context,
                FailureForUnexpectedException(context.Snapshot.Stage),
                dotNetProcessId);
        }
    }

    private async ValueTask<BridgeHostPersistentCutoverResult> FailAsync(
        PersistenceContext context,
        BridgeCutoverFailureReason reason,
        int dotNetProcessId)
    {
        try
        {
            await context.ApplyAndPersistAsync(new CutoverFailedEvent(reason));
            return context.Snapshot.Stage is BridgeHostCutoverStage.RollbackRequired
                ? await RollBackAsync(context, dotNetProcessId)
                : TerminalResult(context);
        }
        catch (CheckpointPersistenceException error)
        {
            return PersistenceFailure(error, context);
        }
    }

    private async ValueTask<BridgeHostPersistentCutoverResult> RollBackAsync(
        PersistenceContext context,
        int dotNetProcessId)
    {
        try
        {
            if (dotNetProcessId > 0)
            {
                await context.ApplyAndPersistAsync(new DotNetStopRequestedEvent());
                var expectedDotNet = new BridgeCutoverHostIdentity(
                    dotNetProcessId,
                    "dotnet",
                    BridgeHostCutoverTransaction.CurrentManagementApiVersion,
                    "active",
                    ActiveOwner: true,
                    context.DotNetInstanceName);
                await operations.RequestExpectedDotNetStopAsync(
                    expectedDotNet,
                    CancellationToken.None);

                await operations.VerifyDotNetOfflineAsync(
                    dotNetProcessId,
                    CancellationToken.None);
                await context.ApplyAndPersistAsync(
                    new DotNetOfflineVerifiedEvent(dotNetProcessId));
            }

            var handoff = await operations.InspectStoreHandoffAsync(
                CancellationToken.None);
            if (handoff is not
                {
                    StoreFlushed: true,
                    StoreCompatible: true,
                    LeaseState: BridgeCutoverLeaseState.Missing,
                })
            {
                await context.PersistRollbackFailedSafeAsync(
                    BridgeCutoverFailureReason.OwnershipUncertain);
                return TerminalResult(context);
            }

            var launchCallbackInvoked = false;
            var nodeProcessId = 0;
            async ValueTask BindNodeProcessAsync(
                int processId,
                CancellationToken cancellationToken)
            {
                if (launchCallbackInvoked || processId <= 0)
                {
                    throw OperationFailure(
                        BridgeCutoverFailureReason.OwnershipUncertain,
                        "Node Active Host 启动 PID 无效或回调被重复调用。");
                }
                await context.ApplyAndPersistAsync(
                    new NodeRollbackStartRequestedEvent(processId));
                nodeProcessId = processId;
                launchCallbackInvoked = true;
            }

            var returnedNodeProcessId = await operations.StartNodeActiveAndBindAsync(
                BindNodeProcessAsync,
                CancellationToken.None);
            if (!launchCallbackInvoked ||
                returnedNodeProcessId != nodeProcessId ||
                nodeProcessId <= 0)
            {
                throw OperationFailure(
                    BridgeCutoverFailureReason.OwnershipUncertain,
                    "Node Active Host 启动 PID 未与持久化检查点绑定。");
            }

            var nodeIdentity = await operations.VerifyNodeActiveAsync(
                nodeProcessId,
                CancellationToken.None);
            await context.ApplyAndPersistAsync(
                new NodeRollbackActiveVerifiedEvent(nodeIdentity));
            return TerminalResult(context);
        }
        catch (CheckpointPersistenceException error)
        {
            return PersistenceFailure(error, context);
        }
        catch (BridgeHostCutoverOperationException error)
        {
            return await PersistRollbackFailureAsync(
                context,
                NormalizeRollbackFailure(error.Reason));
        }
        catch
        {
            return await PersistRollbackFailureAsync(
                context,
                BridgeCutoverFailureReason.OwnershipUncertain);
        }
    }

    private static async ValueTask<BridgeHostPersistentCutoverResult>
        PersistRollbackFailureAsync(
            PersistenceContext context,
            BridgeCutoverFailureReason reason)
    {
        if (context.Snapshot.IsTerminal)
        {
            return TerminalResult(context);
        }
        try
        {
            await context.PersistRollbackFailedSafeAsync(reason);
            return TerminalResult(context);
        }
        catch (CheckpointPersistenceException error)
        {
            return PersistenceFailure(error, context);
        }
    }

    private static BridgeHostPersistentCutoverResult TerminalResult(
        PersistenceContext context) =>
        context.DurableSnapshot.Stage switch
        {
            BridgeHostCutoverStage.Completed => new(
                BridgeHostPersistentCutoverState.Completed,
                context.DurableSnapshot),
            BridgeHostCutoverStage.RolledBack => new(
                BridgeHostPersistentCutoverState.RolledBack,
                context.DurableSnapshot),
            _ => new(
                BridgeHostPersistentCutoverState.FailedSafe,
                context.DurableSnapshot),
        };

    private static BridgeHostPersistentCutoverResult PersistenceFailure(
        CheckpointPersistenceException error,
        PersistenceContext context) =>
        new(error.State, context.DurableSnapshot);

    private static DateTimeOffset GetTimestampBaseline(
        BridgeHostCutoverCheckpointReadResult current,
        out BridgeHostPersistentCutoverState? blockedState)
    {
        blockedState = null;
        if (current.State is BridgeHostCutoverCheckpointReadState.Missing)
        {
            return default;
        }
        if (current.State is BridgeHostCutoverCheckpointReadState.Unavailable)
        {
            blockedState = BridgeHostPersistentCutoverState.Unavailable;
            return default;
        }
        if (current.State is not BridgeHostCutoverCheckpointReadState.Present ||
            current.Checkpoint is null ||
            current.Checkpoint.Stage is not (
                BridgeHostCutoverStage.Completed or
                BridgeHostCutoverStage.RolledBack))
        {
            blockedState =
                BridgeHostPersistentCutoverState.CheckpointRecoveryRequired;
            return default;
        }
        if (current.Checkpoint.UpdatedAt == DateTimeOffset.MaxValue)
        {
            blockedState = BridgeHostPersistentCutoverState.CheckpointConflict;
            return default;
        }
        return current.Checkpoint.UpdatedAt;
    }

    private static BridgeHostPersistentCutoverResult AcquisitionFailure(
        BridgeHostCutoverCheckpointWriterAcquireState state) =>
        state switch
        {
            BridgeHostCutoverCheckpointWriterAcquireState.Busy =>
                new(BridgeHostPersistentCutoverState.Busy),
            BridgeHostCutoverCheckpointWriterAcquireState.RecoveryRequired =>
                new(BridgeHostPersistentCutoverState.CheckpointRecoveryRequired),
            _ => new(BridgeHostPersistentCutoverState.Unavailable),
        };

    private static BridgeHostCutoverOperationException OperationFailure(
        BridgeCutoverFailureReason reason,
        string message) =>
        new(reason, message);

    private static BridgeCutoverFailureReason NormalizeRollbackFailure(
        BridgeCutoverFailureReason reason) =>
        reason is BridgeCutoverFailureReason.None or
            BridgeCutoverFailureReason.InvalidEventOrder or
            BridgeCutoverFailureReason.UnexpectedFailure
            ? BridgeCutoverFailureReason.OwnershipUncertain
            : reason;

    private static BridgeCutoverFailureReason FailureForUnexpectedException(
        BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.NodeStopRequested or
            BridgeHostCutoverStage.NodeOfflineVerified or
            BridgeHostCutoverStage.StoreHandoffVerified
            ? BridgeCutoverFailureReason.OwnershipUncertain
            : BridgeCutoverFailureReason.UnexpectedFailure;

    private static BridgeCutoverFailureReason FailureForOperationException(
        BridgeHostCutoverStage stage,
        BridgeCutoverFailureReason reason,
        int dotNetProcessId)
    {
        if (dotNetProcessId == 0 &&
            stage is BridgeHostCutoverStage.StoreHandoffVerified)
        {
            return BridgeCutoverFailureReason.OwnershipUncertain;
        }
        return reason;
    }

    private sealed class PersistenceContext
    {
        private readonly BridgeHostCutoverTransaction transaction;
        private readonly BridgeHostCutoverCheckpointWriter writer;
        private readonly string operationId;
        private readonly BridgeCutoverHostIdentity expectedNode;
        private readonly string dotNetInstanceName;
        private readonly TimeProvider timeProvider;
        private readonly WriteCheckpointAsync writeCheckpoint;
        private DateTimeOffset lastUpdatedAt;

        public PersistenceContext(
            BridgeHostCutoverTransaction transaction,
            BridgeHostCutoverCheckpointWriter writer,
            string operationId,
            BridgeCutoverHostIdentity expectedNode,
            string dotNetInstanceName,
            DateTimeOffset timestampBaseline,
            TimeProvider timeProvider,
            WriteCheckpointAsync writeCheckpoint)
        {
            this.transaction = transaction;
            this.writer = writer;
            this.operationId = operationId;
            this.expectedNode = expectedNode;
            this.dotNetInstanceName = dotNetInstanceName;
            lastUpdatedAt = timestampBaseline;
            this.timeProvider = timeProvider;
            this.writeCheckpoint = writeCheckpoint;
            DurableSnapshot = transaction.Snapshot;
        }

        public BridgeHostCutoverSnapshot Snapshot => transaction.Snapshot;

        public BridgeHostCutoverSnapshot DurableSnapshot { get; private set; }

        public BridgeCutoverHostIdentity ExpectedNode => expectedNode;

        public string DotNetInstanceName => dotNetInstanceName;

        public async ValueTask ApplyAndPersistAsync(
            BridgeHostCutoverEvent @event)
        {
            var result = transaction.Apply(@event);
            if (!result.Accepted)
            {
                throw new InvalidOperationException(
                    $"持久化切换协调器生成了非法事件顺序：" +
                    $"{@event.GetType().Name} / {transaction.Snapshot.Stage}。");
            }
            if (result.Changed)
            {
                await PersistCurrentAsync(CancellationToken.None);
            }
        }

        public async ValueTask PersistRollbackFailedSafeAsync(
            BridgeCutoverFailureReason reason)
        {
            if (transaction.Snapshot.Stage is not (
                    BridgeHostCutoverStage.RollbackRequired or
                    BridgeHostCutoverStage.DotNetStopRequested or
                    BridgeHostCutoverStage.DotNetOfflineVerified or
                    BridgeHostCutoverStage.NodeRollbackStartRequested))
            {
                throw new InvalidOperationException(
                    "只有未完成的回退阶段才能持久化 FailedSafe。");
            }
            var current = transaction.ExportCheckpoint(
                operationId,
                NextUpdatedAt());
            var checkpoint = current with
            {
                Stage = BridgeHostCutoverStage.FailedSafe,
                RequiresRollback = true,
                FailureReason = NormalizeRollbackFailure(reason),
            };
            await PersistAsync(checkpoint, CancellationToken.None);
        }

        public ValueTask PersistCurrentAsync(
            CancellationToken cancellationToken) =>
            PersistAsync(
                transaction.ExportCheckpoint(operationId, NextUpdatedAt()),
                cancellationToken);

        private async ValueTask PersistAsync(
            BridgeHostCutoverCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            BridgeHostCutoverCheckpointWriteResult result;
            try
            {
                result = await writeCheckpoint(
                    writer,
                    checkpoint,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new CheckpointPersistenceException(
                    BridgeHostPersistentCutoverState.Unavailable);
            }
            if (result.State is not (
                    BridgeHostCutoverCheckpointWriteState.Written or
                    BridgeHostCutoverCheckpointWriteState.Unchanged))
            {
                throw new CheckpointPersistenceException(
                    result.State switch
                    {
                        BridgeHostCutoverCheckpointWriteState.OperationConflict or
                        BridgeHostCutoverCheckpointWriteState.VersionConflict =>
                            BridgeHostPersistentCutoverState.CheckpointConflict,
                        BridgeHostCutoverCheckpointWriteState.InvalidCurrentCheckpoint =>
                            BridgeHostPersistentCutoverState.CheckpointRecoveryRequired,
                        _ => BridgeHostPersistentCutoverState.Unavailable,
                    });
            }
            lastUpdatedAt = checkpoint.UpdatedAt;
            DurableSnapshot = checkpoint.ToSnapshot();
        }

        private DateTimeOffset NextUpdatedAt()
        {
            if (lastUpdatedAt == DateTimeOffset.MaxValue)
            {
                throw new CheckpointPersistenceException(
                    BridgeHostPersistentCutoverState.CheckpointConflict);
            }
            var now = timeProvider.GetUtcNow();
            return now > lastUpdatedAt
                ? now
                : lastUpdatedAt.AddTicks(1);
        }
    }

    private sealed class CheckpointPersistenceException(
        BridgeHostPersistentCutoverState state) : Exception
    {
        public BridgeHostPersistentCutoverState State { get; } = state;
    }
}
