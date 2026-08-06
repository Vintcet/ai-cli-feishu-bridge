namespace AiCliFeishuControl;

internal enum BridgeHostCutoverCheckpointWriterAcquireState
{
    Acquired,
    Busy,
    RecoveryRequired,
    Unavailable,
}

internal sealed record BridgeHostCutoverCheckpointWriterAcquireResult(
    BridgeHostCutoverCheckpointWriterAcquireState State,
    BridgeHostCutoverCheckpointWriter? Writer = null);

internal enum BridgeHostCutoverCheckpointWriteState
{
    Written,
    Unchanged,
    OperationConflict,
    VersionConflict,
    InvalidCurrentCheckpoint,
    Unavailable,
}

internal sealed record BridgeHostCutoverCheckpointWriteResult(
    BridgeHostCutoverCheckpointWriteState State,
    BridgeHostCutoverCheckpointReadState CurrentCheckpointState);

internal sealed class BridgeHostCutoverCheckpointWriter : IDisposable
{
    public const string WriterLockFileName = "bridge-host-cutover.writer.lock";

    private readonly BridgeHostCutoverCheckpointStore store;
    private readonly FileStream lockStream;
    private readonly string operationId;
    private bool disposed;

    private BridgeHostCutoverCheckpointWriter(
        BridgeHostCutoverCheckpointStore store,
        FileStream lockStream,
        string operationId)
    {
        this.store = store;
        this.lockStream = lockStream;
        this.operationId = operationId;
    }

    public static ValueTask<BridgeHostCutoverCheckpointWriterAcquireResult> TryAcquireAsync(
        string dataDirectory,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "检查点写入器的数据目录不能为空。",
                nameof(dataDirectory));
        }
        if (!BridgeHostCutoverCheckpointValidator.IsValidOperationId(operationId))
        {
            throw new InvalidDataException("检查点 operationId 无效。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        try
        {
            Directory.CreateDirectory(fullDataDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                new BridgeHostCutoverCheckpointWriterAcquireResult(
                    BridgeHostCutoverCheckpointWriterAcquireState.Unavailable));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(
                new BridgeHostCutoverCheckpointWriterAcquireResult(
                    BridgeHostCutoverCheckpointWriterAcquireState.Unavailable));
        }

        var preliminaryRecoveryState =
            BridgeHostCutoverCheckpointRecovery.InspectDirectory(fullDataDirectory);
        if (preliminaryRecoveryState is not BridgeHostCutoverCheckpointRecoveryState.Clean)
        {
            return ValueTask.FromResult(RecoveryBlocked(preliminaryRecoveryState));
        }

        var lockPath = Path.Combine(fullDataDirectory, WriterLockFileName);
        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            var recoveryState = BridgeHostCutoverCheckpointRecovery.Inspect(
                fullDataDirectory);
            if (recoveryState is not BridgeHostCutoverCheckpointRecoveryState.Clean)
            {
                return ValueTask.FromResult(RecoveryBlocked(recoveryState));
            }
            var store = new BridgeHostCutoverCheckpointStore(fullDataDirectory);
            var writer = new BridgeHostCutoverCheckpointWriter(
                store,
                lockStream,
                operationId);
            var result = new BridgeHostCutoverCheckpointWriterAcquireResult(
                BridgeHostCutoverCheckpointWriterAcquireState.Acquired,
                writer);
            lockStream = null;
            return ValueTask.FromResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                new BridgeHostCutoverCheckpointWriterAcquireResult(
                    BridgeHostCutoverCheckpointWriterAcquireState.Unavailable));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(
                new BridgeHostCutoverCheckpointWriterAcquireResult(
                    BridgeHostCutoverCheckpointWriterAcquireState.Busy));
        }
        finally
        {
            lockStream?.Dispose();
        }
    }

    internal static ValueTask<BridgeHostCutoverCheckpointRecoveryResult>
        TryRecoverOrphanedFilesAsync(
            string dataDirectory,
            CancellationToken cancellationToken = default) =>
        BridgeHostCutoverCheckpointRecovery.TryQuarantineOrphanedFilesAsync(
            dataDirectory,
            cancellationToken);

    public async ValueTask<BridgeHostCutoverCheckpointWriteResult> TryWriteAsync(
        BridgeHostCutoverCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var current = await store.ReadAsync(cancellationToken);
        if (!string.Equals(
                checkpoint.OperationId,
                operationId,
                StringComparison.Ordinal))
        {
            return Conflict(current.State);
        }
        switch (current.State)
        {
            case BridgeHostCutoverCheckpointReadState.Missing:
                if (checkpoint.Stage is not BridgeHostCutoverStage.Planned)
                {
                    return Conflict(current.State);
                }
                return await WriteIfCurrentVersionAsync(
                    checkpoint,
                    current,
                    cancellationToken);

            case BridgeHostCutoverCheckpointReadState.Invalid:
                return new(
                    BridgeHostCutoverCheckpointWriteState.InvalidCurrentCheckpoint,
                    current.State);

            case BridgeHostCutoverCheckpointReadState.Unavailable:
                return new(
                    BridgeHostCutoverCheckpointWriteState.Unavailable,
                    current.State);

            case BridgeHostCutoverCheckpointReadState.Present:
                return await TryWritePresentAsync(current, checkpoint, cancellationToken);

            default:
                return new(
                    BridgeHostCutoverCheckpointWriteState.InvalidCurrentCheckpoint,
                    BridgeHostCutoverCheckpointReadState.Invalid);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lockStream.Dispose();
    }

    private async ValueTask<BridgeHostCutoverCheckpointWriteResult> TryWritePresentAsync(
        BridgeHostCutoverCheckpointReadResult current,
        BridgeHostCutoverCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (current.Checkpoint is null)
        {
            return new(
                BridgeHostCutoverCheckpointWriteState.InvalidCurrentCheckpoint,
                current.State);
        }
        var existing = current.Checkpoint;
        if (existing == checkpoint)
        {
            return new(
                BridgeHostCutoverCheckpointWriteState.Unchanged,
                current.State);
        }

        if (!string.Equals(
                existing.OperationId,
                operationId,
                StringComparison.Ordinal))
        {
            if (!CanStartNewOperation(existing, checkpoint))
            {
                return Conflict(current.State);
            }

            return await WriteIfCurrentVersionAsync(
                checkpoint,
                current,
                cancellationToken);
        }

        if (!BridgeHostCutoverCheckpointTransition.IsAllowed(existing, checkpoint))
        {
            return Conflict(current.State);
        }

        return await WriteIfCurrentVersionAsync(
            checkpoint,
            current,
            cancellationToken);
    }

    private async ValueTask<BridgeHostCutoverCheckpointWriteResult>
        WriteIfCurrentVersionAsync(
            BridgeHostCutoverCheckpoint checkpoint,
            BridgeHostCutoverCheckpointReadResult current,
            CancellationToken cancellationToken)
    {
        var result = await store.TryWriteIfVersionAsync(
            checkpoint,
            current,
            cancellationToken);
        return result switch
        {
            BridgeHostCutoverCheckpointStore.CompareAndSwapState.Written =>
                Written(current.State),
            BridgeHostCutoverCheckpointStore.CompareAndSwapState.VersionConflict =>
                new(
                    BridgeHostCutoverCheckpointWriteState.VersionConflict,
                    current.State),
            BridgeHostCutoverCheckpointStore.CompareAndSwapState.Unavailable =>
                new(
                    BridgeHostCutoverCheckpointWriteState.Unavailable,
                    current.State),
            _ => new(
                BridgeHostCutoverCheckpointWriteState.VersionConflict,
                current.State),
        };
    }

    private static bool CanStartNewOperation(
        BridgeHostCutoverCheckpoint existing,
        BridgeHostCutoverCheckpoint next) =>
        next.Stage is BridgeHostCutoverStage.Planned &&
        (existing.Stage is BridgeHostCutoverStage.Completed or
            BridgeHostCutoverStage.RolledBack) &&
        next.UpdatedAt > existing.UpdatedAt &&
        !existing.RequiresRollback;

    private static BridgeHostCutoverCheckpointWriteResult Written(
        BridgeHostCutoverCheckpointReadState currentState) =>
        new(BridgeHostCutoverCheckpointWriteState.Written, currentState);

    private static BridgeHostCutoverCheckpointWriteResult Conflict(
        BridgeHostCutoverCheckpointReadState currentState) =>
        new(BridgeHostCutoverCheckpointWriteState.OperationConflict, currentState);

    private static BridgeHostCutoverCheckpointWriterAcquireResult RecoveryBlocked(
        BridgeHostCutoverCheckpointRecoveryState recoveryState) =>
        new(
            recoveryState is BridgeHostCutoverCheckpointRecoveryState.Unavailable
                ? BridgeHostCutoverCheckpointWriterAcquireState.Unavailable
                : BridgeHostCutoverCheckpointWriterAcquireState.RecoveryRequired);

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BridgeHostCutoverCheckpointWriter));
        }
    }
}

internal static class BridgeHostCutoverCheckpointTransition
{
    public static bool IsAllowed(
        BridgeHostCutoverCheckpoint current,
        BridgeHostCutoverCheckpoint next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        if (!BridgeHostCutoverCheckpointValidator.IsValid(current) ||
            !BridgeHostCutoverCheckpointValidator.IsValid(next) ||
            !string.Equals(
                current.OperationId,
                next.OperationId,
                StringComparison.Ordinal) ||
            current.ExpectedNode is null ||
            !current.ExpectedNode.Equals(next.ExpectedNode) ||
            !string.Equals(
                current.ExpectedDotNetInstanceName,
                next.ExpectedDotNetInstanceName,
                StringComparison.Ordinal) ||
            next.UpdatedAt <= current.UpdatedAt)
        {
            return false;
        }

        return current.Stage switch
        {
            BridgeHostCutoverStage.Planned =>
                next.Stage is BridgeHostCutoverStage.NodeStopRequested or
                    BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.NodeStopRequested =>
                next.Stage is BridgeHostCutoverStage.NodeOfflineVerified or
                    BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.NodeOfflineVerified =>
                next.Stage is BridgeHostCutoverStage.StoreHandoffVerified or
                    BridgeHostCutoverStage.RollbackRequired or
                    BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.StoreHandoffVerified =>
                next.Stage is BridgeHostCutoverStage.DotNetStartRequested or
                    BridgeHostCutoverStage.RollbackRequired or
                    BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.DotNetStartRequested =>
                next.Stage is BridgeHostCutoverStage.DotNetActiveVerified or
                    BridgeHostCutoverStage.RollbackRequired,
            BridgeHostCutoverStage.DotNetActiveVerified =>
                next.Stage is BridgeHostCutoverStage.Completed or
                    BridgeHostCutoverStage.RollbackRequired,
            BridgeHostCutoverStage.RollbackRequired =>
                current.DotNetProcessId > 0
                    ? next.Stage is BridgeHostCutoverStage.DotNetStopRequested or
                        BridgeHostCutoverStage.FailedSafe
                    : next.Stage is BridgeHostCutoverStage.NodeRollbackStartRequested or
                        BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.DotNetStopRequested =>
                next.Stage is BridgeHostCutoverStage.DotNetOfflineVerified or
                    BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.DotNetOfflineVerified =>
                next.Stage is BridgeHostCutoverStage.NodeRollbackStartRequested or
                    BridgeHostCutoverStage.FailedSafe,
            BridgeHostCutoverStage.NodeRollbackStartRequested =>
                next.Stage is BridgeHostCutoverStage.RolledBack or
                    BridgeHostCutoverStage.FailedSafe,
            _ => false,
        };
    }
}
