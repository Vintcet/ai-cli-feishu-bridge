namespace AiCliFeishuControl;

internal interface IBridgeHostCutoverOperations
{
    ValueTask RequestNodeStopAsync(
        BridgeCutoverHostIdentity expectedNode,
        CancellationToken cancellationToken);

    ValueTask VerifyNodeOfflineAsync(
        int expectedProcessId,
        CancellationToken cancellationToken);

    ValueTask<BridgeStoreHandoffEvidence> InspectStoreHandoffAsync(
        CancellationToken cancellationToken);

    ValueTask<int> StartDotNetActiveAsync(
        string instanceName,
        CancellationToken cancellationToken);

    ValueTask<BridgeCutoverHostIdentity> VerifyDotNetActiveAsync(
        int expectedProcessId,
        string expectedInstanceName,
        CancellationToken cancellationToken);

    ValueTask RequestDotNetStopAsync(
        int expectedProcessId,
        CancellationToken cancellationToken);

    ValueTask VerifyDotNetOfflineAsync(
        int expectedProcessId,
        CancellationToken cancellationToken);

    ValueTask<int> StartNodeActiveAsync(CancellationToken cancellationToken);

    ValueTask<BridgeCutoverHostIdentity> VerifyNodeActiveAsync(
        int expectedProcessId,
        CancellationToken cancellationToken);
}

internal sealed class BridgeHostCutoverOperationException : Exception
{
    public BridgeHostCutoverOperationException(
        BridgeCutoverFailureReason reason,
        string message) : base(message)
    {
        if (reason is BridgeCutoverFailureReason.None or
            BridgeCutoverFailureReason.InvalidEventOrder)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        Reason = reason;
    }

    public BridgeCutoverFailureReason Reason { get; }
}

internal sealed record BridgeHostCutoverRunResult(
    BridgeHostCutoverSnapshot Snapshot)
{
    public bool Completed => Snapshot.Stage is BridgeHostCutoverStage.Completed;

    public bool RolledBack => Snapshot.Stage is BridgeHostCutoverStage.RolledBack;
}

internal sealed class BridgeHostCutoverCoordinator(
    IBridgeHostCutoverOperations operations)
{
    private readonly IBridgeHostCutoverOperations operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    public async ValueTask<BridgeHostCutoverRunResult> RunAsync(
        BridgeCutoverHostIdentity expectedNode,
        string dotNetInstanceName,
        CancellationToken cancellationToken = default)
    {
        var transaction = BridgeHostCutoverTransaction.Create(
            expectedNode,
            dotNetInstanceName);
        var dotNetProcessId = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyRequired(transaction, new NodeStopRequestedEvent(expectedNode));
            await operations.RequestNodeStopAsync(
                expectedNode,
                CancellationToken.None);

            await operations.VerifyNodeOfflineAsync(
                expectedNode.ProcessId,
                CancellationToken.None);
            ApplyRequired(
                transaction,
                new NodeOfflineVerifiedEvent(expectedNode.ProcessId));

            var handoff = await operations.InspectStoreHandoffAsync(
                CancellationToken.None);
            ApplyRequired(transaction, new StoreHandoffVerifiedEvent(handoff));
            if (transaction.Snapshot.Stage is BridgeHostCutoverStage.RollbackRequired)
            {
                return await RollBackAsync(transaction, dotNetProcessId);
            }
            if (transaction.Snapshot.IsTerminal)
            {
                return new(transaction.Snapshot);
            }

            dotNetProcessId = await operations.StartDotNetActiveAsync(
                dotNetInstanceName,
                CancellationToken.None);
            ApplyRequired(
                transaction,
                new DotNetStartRequestedEvent(dotNetProcessId));
            if (transaction.Snapshot.Stage is BridgeHostCutoverStage.RollbackRequired)
            {
                return await RollBackAsync(transaction, dotNetProcessId);
            }
            if (transaction.Snapshot.IsTerminal)
            {
                return new(transaction.Snapshot);
            }

            var dotNetIdentity = await operations.VerifyDotNetActiveAsync(
                dotNetProcessId,
                dotNetInstanceName,
                CancellationToken.None);
            ApplyRequired(
                transaction,
                new DotNetActiveVerifiedEvent(dotNetIdentity));
            if (transaction.Snapshot.Stage is BridgeHostCutoverStage.RollbackRequired)
            {
                return await RollBackAsync(transaction, dotNetProcessId);
            }

            ApplyRequired(transaction, new CutoverCompletedEvent());
            return new(transaction.Snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(
                transaction,
                transaction.Snapshot.Stage is BridgeHostCutoverStage.Planned
                    ? BridgeCutoverFailureReason.Cancelled
                    : FailureForUnexpectedException(transaction.Snapshot.Stage),
                dotNetProcessId);
        }
        catch (BridgeHostCutoverOperationException error)
        {
            return await FailAsync(
                transaction,
                FailureForOperationException(
                    transaction.Snapshot.Stage,
                    error.Reason,
                    dotNetProcessId),
                dotNetProcessId);
        }
        catch
        {
            return await FailAsync(
                transaction,
                FailureForUnexpectedException(transaction.Snapshot.Stage),
                dotNetProcessId);
        }
    }

    private async ValueTask<BridgeHostCutoverRunResult> FailAsync(
        BridgeHostCutoverTransaction transaction,
        BridgeCutoverFailureReason reason,
        int dotNetProcessId)
    {
        ApplyRequired(transaction, new CutoverFailedEvent(reason));
        if (transaction.Snapshot.Stage is not BridgeHostCutoverStage.RollbackRequired)
        {
            return new(transaction.Snapshot);
        }

        return await RollBackAsync(transaction, dotNetProcessId);
    }

    private async ValueTask<BridgeHostCutoverRunResult> RollBackAsync(
        BridgeHostCutoverTransaction transaction,
        int dotNetProcessId)
    {
        try
        {
            if (dotNetProcessId > 0)
            {
                await operations.RequestDotNetStopAsync(
                    dotNetProcessId,
                    CancellationToken.None);
                ApplyRequired(transaction, new DotNetStopRequestedEvent());

                await operations.VerifyDotNetOfflineAsync(
                    dotNetProcessId,
                    CancellationToken.None);
                ApplyRequired(
                    transaction,
                    new DotNetOfflineVerifiedEvent(dotNetProcessId));
            }

            var nodeProcessId = await operations.StartNodeActiveAsync(
                CancellationToken.None);
            ApplyRequired(
                transaction,
                new NodeRollbackStartRequestedEvent(nodeProcessId));
            if (transaction.Snapshot.IsTerminal)
            {
                return new(transaction.Snapshot);
            }

            var nodeIdentity = await operations.VerifyNodeActiveAsync(
                nodeProcessId,
                CancellationToken.None);
            ApplyRequired(
                transaction,
                new NodeRollbackActiveVerifiedEvent(nodeIdentity));
            return new(transaction.Snapshot);
        }
        catch (BridgeHostCutoverOperationException error)
        {
            ApplyRequired(
                transaction,
                new CutoverFailedEvent(
                    NormalizeRollbackFailure(error.Reason)));
            return new(transaction.Snapshot);
        }
        catch
        {
            ApplyRequired(
                transaction,
                new CutoverFailedEvent(
                    BridgeCutoverFailureReason.OwnershipUncertain));
            return new(transaction.Snapshot);
        }
    }

    private static void ApplyRequired(
        BridgeHostCutoverTransaction transaction,
        BridgeHostCutoverEvent @event)
    {
        var result = transaction.Apply(@event);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"切换协调器生成了非法事件顺序：{@event.GetType().Name} / " +
                $"{transaction.Snapshot.Stage}。");
        }
    }

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
}
