using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

internal interface IBridgeHostRecoveryOperations : IBridgeHostCutoverOperations
{
    ValueTask RequestExpectedDotNetStopAsync(
        BridgeCutoverHostIdentity expectedDotNet,
        CancellationToken cancellationToken);
}

internal enum BridgeHostRecoveryExecutionState
{
    NoActionRequired,
    Recovered,
    ManualIntervention,
    Busy,
    CheckpointRecoveryRequired,
    Unavailable,
    UnsafeStoreHandoff,
    FailedSafe,
}

internal sealed record BridgeHostRecoveryExecutionResult(
    BridgeHostRecoveryExecutionState State,
    BridgeHostRecoveryPlan? Plan = null);

internal sealed class BridgeHostRecoveryExecutor
{
    private readonly BridgeHostCutoverCheckpointStore checkpointStore;
    private readonly BridgeHostRecoveryObserver observer;
    private readonly IBridgeHostRecoveryOperations operations;
    private readonly string dataDirectory;

    public BridgeHostRecoveryExecutor(
        string dataDirectory,
        BridgeHostRecoveryObserver observer,
        IBridgeHostRecoveryOperations operations)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "恢复执行器的数据目录不能为空。",
                nameof(dataDirectory));
        }

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        checkpointStore = new BridgeHostCutoverCheckpointStore(this.dataDirectory);
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public async ValueTask<BridgeHostRecoveryExecutionResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preliminary = await checkpointStore.ReadAsync(cancellationToken);
        if (!TryGetCheckpoint(
                preliminary,
                out var preliminaryCheckpoint,
                out var preliminaryFailure))
        {
            return Manual(preliminaryFailure);
        }

        var acquisition = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
            dataDirectory,
            preliminaryCheckpoint.OperationId,
            cancellationToken);
        if (acquisition.State is not BridgeHostCutoverCheckpointWriterAcquireState.Acquired ||
            acquisition.Writer is null)
        {
            return acquisition.State switch
            {
                BridgeHostCutoverCheckpointWriterAcquireState.Busy =>
                    new(BridgeHostRecoveryExecutionState.Busy),
                BridgeHostCutoverCheckpointWriterAcquireState.RecoveryRequired =>
                    new(BridgeHostRecoveryExecutionState.CheckpointRecoveryRequired),
                _ => new(BridgeHostRecoveryExecutionState.Unavailable),
            };
        }

        using var writer = acquisition.Writer;
        cancellationToken.ThrowIfCancellationRequested();
        var locked = await checkpointStore.ReadAsync(cancellationToken);
        if (!IsSameCheckpoint(preliminary, locked))
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }

        var inspection = await observer.InspectAsync(cancellationToken);
        if (inspection.CheckpointState is not
                BridgeHostCutoverCheckpointReadState.Present)
        {
            return new(
                BridgeHostRecoveryExecutionState.ManualIntervention,
                inspection.Plan);
        }

        var confirmed = await checkpointStore.ReadAsync(cancellationToken);
        if (!IsSameCheckpoint(locked, confirmed))
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }
        if (inspection.Plan.RequiresManualIntervention)
        {
            return new(
                BridgeHostRecoveryExecutionState.ManualIntervention,
                inspection.Plan);
        }
        if (string.IsNullOrEmpty(locked.FileVersion) ||
            !string.Equals(
                inspection.CheckpointFileVersion,
                locked.FileVersion,
                StringComparison.Ordinal))
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }
        if (!PlanMatchesCheckpoint(inspection.Plan, preliminaryCheckpoint))
        {
            return Manual(BridgeHostRecoveryReason.RecoveryTargetUnbound);
        }

        return await ExecuteAsync(
            inspection.Plan,
            preliminaryCheckpoint,
            locked.FileVersion,
            writer,
            cancellationToken);
    }

    private async ValueTask<BridgeHostRecoveryExecutionResult> ExecuteAsync(
        BridgeHostRecoveryPlan plan,
        BridgeHostCutoverCheckpoint checkpoint,
        string checkpointFileVersion,
        BridgeHostCutoverCheckpointWriter writer,
        CancellationToken cancellationToken)
    {
        if (plan.Disposition is BridgeHostRecoveryDisposition.NodeAlreadyActive or
            BridgeHostRecoveryDisposition.DotNetAlreadyActive)
        {
            return new(
                BridgeHostRecoveryExecutionState.NoActionRequired,
                plan);
        }

        var ownershipSideEffectsStarted = false;
        void MarkOwnershipSideEffectsStarted() => ownershipSideEffectsStarted = true;

        try
        {
            return plan.Disposition switch
            {
                BridgeHostRecoveryDisposition.RestartNode =>
                    await RestartNodeAsync(
                        checkpoint,
                        checkpointFileVersion,
                        writer,
                        plan,
                        MarkOwnershipSideEffectsStarted,
                        cancellationToken),
                BridgeHostRecoveryDisposition.RestartDotNet =>
                    await RestartDotNetAsync(
                        checkpoint,
                        checkpointFileVersion,
                        plan,
                        MarkOwnershipSideEffectsStarted,
                        cancellationToken),
                BridgeHostRecoveryDisposition.RollBackDotNetToNode =>
                    await RollBackDotNetToNodeAsync(
                        checkpoint,
                        checkpointFileVersion,
                        writer,
                        plan,
                        MarkOwnershipSideEffectsStarted,
                        cancellationToken),
                _ => new(
                    BridgeHostRecoveryExecutionState.ManualIntervention,
                    plan),
            };
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested &&
            !ownershipSideEffectsStarted)
        {
            throw;
        }
        catch
        {
            return new(BridgeHostRecoveryExecutionState.FailedSafe, plan);
        }
    }

    private async ValueTask<BridgeHostRecoveryExecutionResult> RestartNodeAsync(
        BridgeHostCutoverCheckpoint checkpoint,
        string checkpointFileVersion,
        BridgeHostCutoverCheckpointWriter writer,
        BridgeHostRecoveryPlan plan,
        Action markOwnershipSideEffectsStarted,
        CancellationToken cancellationToken)
    {
        if (!await HasSafeStoreHandoffAsync(cancellationToken))
        {
            return new(BridgeHostRecoveryExecutionState.UnsafeStoreHandoff, plan);
        }
        if (!await IsCheckpointVersionCurrentAsync(
                checkpointFileVersion,
                cancellationToken))
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }

        cancellationToken.ThrowIfCancellationRequested();
        markOwnershipSideEffectsStarted();
        var processId = await operations.StartNodeActiveAsync(CancellationToken.None);
        var identity = await VerifyRecoveredNodeAsync(
            processId,
            checkpoint.ExpectedNode.InstanceName,
            checkpointFileVersion);
        if (checkpoint.Stage is BridgeHostCutoverStage.RolledBack)
        {
            return new(BridgeHostRecoveryExecutionState.Recovered, plan);
        }
        return await ConvergeRecoveredNodeAsync(
            writer,
            identity,
            checkpointFileVersion,
            plan);
    }

    private async ValueTask<BridgeHostRecoveryExecutionResult> RestartDotNetAsync(
        BridgeHostCutoverCheckpoint checkpoint,
        string checkpointFileVersion,
        BridgeHostRecoveryPlan plan,
        Action markOwnershipSideEffectsStarted,
        CancellationToken cancellationToken)
    {
        if (!await HasSafeStoreHandoffAsync(cancellationToken))
        {
            return new(BridgeHostRecoveryExecutionState.UnsafeStoreHandoff, plan);
        }
        if (!await IsCheckpointVersionCurrentAsync(
                checkpointFileVersion,
                cancellationToken))
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }

        cancellationToken.ThrowIfCancellationRequested();
        markOwnershipSideEffectsStarted();
        var processId = await operations.StartDotNetActiveAsync(
            checkpoint.ExpectedDotNetInstanceName,
            CancellationToken.None);
        await VerifyRecoveredDotNetAsync(
            processId,
            checkpoint.ExpectedDotNetInstanceName,
            checkpointFileVersion);
        return new(BridgeHostRecoveryExecutionState.Recovered, plan);
    }

    private async ValueTask<BridgeHostRecoveryExecutionResult>
        RollBackDotNetToNodeAsync(
            BridgeHostCutoverCheckpoint checkpoint,
            string checkpointFileVersion,
            BridgeHostCutoverCheckpointWriter writer,
            BridgeHostRecoveryPlan plan,
            Action markOwnershipSideEffectsStarted,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await IsCheckpointVersionCurrentAsync(
                checkpointFileVersion,
                cancellationToken))
        {
            return Manual(BridgeHostRecoveryReason.CheckpointChanged);
        }

        var expectedDotNet = new BridgeCutoverHostIdentity(
            checkpoint.DotNetProcessId,
            "dotnet",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            checkpoint.ExpectedDotNetInstanceName);
        markOwnershipSideEffectsStarted();
        await operations.RequestExpectedDotNetStopAsync(
            expectedDotNet,
            CancellationToken.None);
        await operations.VerifyDotNetOfflineAsync(
            expectedDotNet.ProcessId,
            CancellationToken.None);

        if (!await HasSafeStoreHandoffAsync(CancellationToken.None))
        {
            return new(BridgeHostRecoveryExecutionState.UnsafeStoreHandoff, plan);
        }
        if (!await IsCheckpointVersionCurrentAsync(
                checkpointFileVersion,
                CancellationToken.None))
        {
            return new(BridgeHostRecoveryExecutionState.FailedSafe, plan);
        }

        var processId = await operations.StartNodeActiveAsync(CancellationToken.None);
        var identity = await VerifyRecoveredNodeAsync(
            processId,
            checkpoint.ExpectedNode.InstanceName,
            checkpointFileVersion);
        return await ConvergeRecoveredNodeAsync(
            writer,
            identity,
            checkpointFileVersion,
            plan);
    }

    private async ValueTask<BridgeCutoverHostIdentity> VerifyRecoveredNodeAsync(
        int processId,
        string instanceName,
        string checkpointFileVersion)
    {
        var first = await operations.VerifyNodeActiveAsync(
            processId,
            CancellationToken.None);
        if (!IsExpectedNode(first, processId, instanceName))
        {
            throw new InvalidOperationException(
                "恢复后的 Node Active Owner 身份无效。");
        }

        var inspection = await observer.InspectAsync(CancellationToken.None);
        if (!IsStablePostStartInspection(
                inspection,
                checkpointFileVersion,
                BridgeHostRecoveryDisposition.NodeAlreadyActive))
        {
            throw new InvalidOperationException(
                "恢复后的 Node 端点与 Active Owner 租约未稳定收敛。");
        }

        var second = await operations.VerifyNodeActiveAsync(
            processId,
            CancellationToken.None);
        if (!IsExpectedNode(second, processId, instanceName) ||
            !first.Matches(second))
        {
            throw new InvalidOperationException(
                "恢复后的 Node Active Owner 身份未稳定收敛。");
        }
        return second;
    }

    private async ValueTask VerifyRecoveredDotNetAsync(
        int processId,
        string instanceName,
        string checkpointFileVersion)
    {
        var first = await operations.VerifyDotNetActiveAsync(
            processId,
            instanceName,
            CancellationToken.None);
        if (!IsExpectedDotNet(first, processId, instanceName))
        {
            throw new InvalidOperationException(
                "恢复后的 .NET Active Owner 身份无效。");
        }

        var inspection = await observer.InspectAsync(CancellationToken.None);
        if (!IsStablePostStartInspection(
                inspection,
                checkpointFileVersion,
                BridgeHostRecoveryDisposition.DotNetAlreadyActive))
        {
            throw new InvalidOperationException(
                "恢复后的 .NET 端点与 Active Owner 租约未稳定收敛。");
        }

        var second = await operations.VerifyDotNetActiveAsync(
            processId,
            instanceName,
            CancellationToken.None);
        if (!IsExpectedDotNet(second, processId, instanceName) ||
            !first.Matches(second) ||
            !await IsCheckpointVersionCurrentAsync(
                checkpointFileVersion,
                CancellationToken.None))
        {
            throw new InvalidOperationException(
                "恢复后的 .NET Active Owner 身份未稳定收敛。");
        }
    }

    private static bool IsStablePostStartInspection(
        BridgeHostRecoveryInspection inspection,
        string checkpointFileVersion,
        BridgeHostRecoveryDisposition expectedDisposition) =>
        inspection.CheckpointState is BridgeHostCutoverCheckpointReadState.Present &&
        string.Equals(
            inspection.CheckpointFileVersion,
            checkpointFileVersion,
            StringComparison.Ordinal) &&
        inspection.Plan.Reason is BridgeHostRecoveryReason.None &&
        inspection.Plan.Disposition == expectedDisposition;

    private static async ValueTask<BridgeHostRecoveryExecutionResult>
        ConvergeRecoveredNodeAsync(
            BridgeHostCutoverCheckpointWriter writer,
            BridgeCutoverHostIdentity recoveredNode,
            string checkpointFileVersion,
            BridgeHostRecoveryPlan plan)
    {
        var write = await writer.TryConvergeRecoveryToNodeAsync(
            recoveredNode,
            checkpointFileVersion,
            CancellationToken.None);
        return write.State is BridgeHostCutoverCheckpointWriteState.Written or
            BridgeHostCutoverCheckpointWriteState.Unchanged
            ? new(BridgeHostRecoveryExecutionState.Recovered, plan)
            : new(BridgeHostRecoveryExecutionState.FailedSafe, plan);
    }

    private async ValueTask<bool> HasSafeStoreHandoffAsync(
        CancellationToken cancellationToken)
    {
        var evidence = await operations.InspectStoreHandoffAsync(cancellationToken);
        return evidence is
        {
            StoreFlushed: true,
            StoreCompatible: true,
            LeaseState: BridgeCutoverLeaseState.Missing,
        };
    }

    private async ValueTask<bool> IsCheckpointVersionCurrentAsync(
        string expectedFileVersion,
        CancellationToken cancellationToken)
    {
        var current = await checkpointStore.ReadAsync(cancellationToken);
        return current.State is BridgeHostCutoverCheckpointReadState.Present &&
            string.Equals(
                current.FileVersion,
                expectedFileVersion,
                StringComparison.Ordinal);
    }

    private static bool PlanMatchesCheckpoint(
        BridgeHostRecoveryPlan plan,
        BridgeHostCutoverCheckpoint checkpoint)
    {
        if (plan.Reason is not BridgeHostRecoveryReason.None ||
            !Enum.IsDefined(plan.Disposition))
        {
            return false;
        }

        var committed = checkpoint.Stage is BridgeHostCutoverStage.Completed;
        return plan.Disposition switch
        {
            BridgeHostRecoveryDisposition.NodeAlreadyActive => !committed,
            BridgeHostRecoveryDisposition.DotNetAlreadyActive => committed,
            BridgeHostRecoveryDisposition.RestartNode => !committed,
            BridgeHostRecoveryDisposition.RestartDotNet => committed,
            BridgeHostRecoveryDisposition.RollBackDotNetToNode =>
                !committed && checkpoint.DotNetProcessId > 0,
            _ => false,
        };
    }

    private static bool IsExpectedNode(
        BridgeCutoverHostIdentity? identity,
        int processId,
        string instanceName) =>
        processId > 0 &&
        identity is not null &&
        identity.ProcessId == processId &&
        identity.IsNodeActive(
            BridgeHostCutoverTransaction.CurrentManagementApiVersion) &&
        string.Equals(identity.InstanceName, instanceName, StringComparison.Ordinal);

    private static bool IsExpectedDotNet(
        BridgeCutoverHostIdentity? identity,
        int processId,
        string instanceName) =>
        processId > 0 &&
        identity is not null &&
        identity.ProcessId == processId &&
        identity.IsDotNetActive(
            BridgeHostCutoverTransaction.CurrentManagementApiVersion) &&
        string.Equals(identity.InstanceName, instanceName, StringComparison.Ordinal);

    private static bool IsSameCheckpoint(
        BridgeHostCutoverCheckpointReadResult expected,
        BridgeHostCutoverCheckpointReadResult actual) =>
        expected.State is BridgeHostCutoverCheckpointReadState.Present &&
        actual.State is BridgeHostCutoverCheckpointReadState.Present &&
        expected.Checkpoint == actual.Checkpoint &&
        string.Equals(
            expected.FileVersion,
            actual.FileVersion,
            StringComparison.Ordinal);

    private static bool TryGetCheckpoint(
        BridgeHostCutoverCheckpointReadResult result,
        out BridgeHostCutoverCheckpoint checkpoint,
        out BridgeHostRecoveryReason failure)
    {
        checkpoint = null!;
        failure = result.State switch
        {
            BridgeHostCutoverCheckpointReadState.Missing =>
                BridgeHostRecoveryReason.CheckpointMissing,
            BridgeHostCutoverCheckpointReadState.Unavailable =>
                BridgeHostRecoveryReason.CheckpointUnavailable,
            _ => BridgeHostRecoveryReason.InvalidCheckpoint,
        };
        if (result.State is not BridgeHostCutoverCheckpointReadState.Present ||
            !BridgeHostCutoverCheckpointValidator.IsValid(result.Checkpoint))
        {
            return false;
        }

        checkpoint = result.Checkpoint!;
        return true;
    }

    private static BridgeHostRecoveryExecutionResult Manual(
        BridgeHostRecoveryReason reason) =>
        new(
            BridgeHostRecoveryExecutionState.ManualIntervention,
            new BridgeHostRecoveryPlan(
                BridgeHostRecoveryDisposition.ManualIntervention,
                reason));
}
