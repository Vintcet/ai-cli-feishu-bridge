using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostCutoverTransactionTests
{
    private const int NodeProcessId = 400;
    private const int DotNetProcessId = 500;
    private const int RollbackNodeProcessId = 600;
    private const string DotNetInstanceName = "production-dotnet";

    [TestMethod]
    public void CutoverRequiresAuthenticatedNodeAndValidDotNetInstance()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            BridgeHostCutoverTransaction.Create(
                Node(NodeProcessId) with { ActiveOwner = false },
                DotNetInstanceName));
        Assert.ThrowsException<ArgumentException>(() =>
            BridgeHostCutoverTransaction.Create(Node(NodeProcessId), "invalid instance"));
    }

    [TestMethod]
    public void CutoverCompletesOnlyAfterOrderedOfflineHandoffAndIdentityEvidence()
    {
        var transaction = CreateTransaction();

        AssertAdvance(
            transaction,
            new NodeStopRequestedEvent(Node(NodeProcessId)),
            BridgeHostCutoverStage.NodeStopRequested);
        AssertAdvance(
            transaction,
            new NodeOfflineVerifiedEvent(NodeProcessId),
            BridgeHostCutoverStage.NodeOfflineVerified);
        AssertAdvance(
            transaction,
            new StoreHandoffVerifiedEvent(Handoff(BridgeCutoverLeaseState.Missing)),
            BridgeHostCutoverStage.StoreHandoffVerified);
        AssertAdvance(
            transaction,
            new DotNetStartRequestedEvent(DotNetProcessId),
            BridgeHostCutoverStage.DotNetStartRequested);
        AssertAdvance(
            transaction,
            new DotNetActiveVerifiedEvent(DotNet(DotNetProcessId)),
            BridgeHostCutoverStage.DotNetActiveVerified);
        AssertAdvance(
            transaction,
            new CutoverCompletedEvent(),
            BridgeHostCutoverStage.Completed);

        Assert.IsTrue(transaction.Snapshot.IsTerminal);
        Assert.IsFalse(transaction.Snapshot.RequiresRollback);
        Assert.AreEqual(BridgeCutoverFailureReason.None, transaction.Snapshot.FailureReason);
    }

    [TestMethod]
    public void OutOfOrderEventsAreRejectedWithoutChangingState()
    {
        BridgeHostCutoverEvent[] events =
        [
            new NodeOfflineVerifiedEvent(NodeProcessId),
            new StoreHandoffVerifiedEvent(Handoff(BridgeCutoverLeaseState.Missing)),
            new DotNetStartRequestedEvent(DotNetProcessId),
            new DotNetActiveVerifiedEvent(DotNet(DotNetProcessId)),
            new CutoverCompletedEvent(),
            new DotNetStopRequestedEvent(),
            new DotNetOfflineVerifiedEvent(DotNetProcessId),
            new NodeRollbackStartRequestedEvent(RollbackNodeProcessId),
            new NodeRollbackActiveVerifiedEvent(Node(RollbackNodeProcessId)),
        ];

        foreach (var @event in events)
        {
            var transaction = CreateTransaction();
            var result = transaction.Apply(@event);

            Assert.IsFalse(result.Accepted, @event.GetType().Name);
            Assert.IsFalse(result.Changed, @event.GetType().Name);
            Assert.AreEqual(
                BridgeCutoverFailureReason.InvalidEventOrder,
                result.Reason,
                @event.GetType().Name);
            Assert.AreEqual(
                BridgeHostCutoverStage.Planned,
                transaction.Snapshot.Stage,
                @event.GetType().Name);
        }
    }

    [TestMethod]
    public void NodeIdentityMismatchFailsSafeBeforeStoreHandoff()
    {
        var transaction = CreateTransaction();

        var result = transaction.Apply(
            new NodeStopRequestedEvent(Node(NodeProcessId + 1)));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, transaction.Snapshot.Stage);
        Assert.AreEqual(
            BridgeCutoverFailureReason.NodeIdentityMismatch,
            transaction.Snapshot.FailureReason);
        Assert.IsFalse(transaction.Snapshot.RequiresRollback);
    }

    [TestMethod]
    public void NodeExitFailureCannotReachStoreHandoff()
    {
        var transaction = CreateTransaction();
        transaction.Apply(new NodeStopRequestedEvent(Node(NodeProcessId)));

        var result = transaction.Apply(
            new CutoverFailedEvent(BridgeCutoverFailureReason.NodeStillOnline));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, transaction.Snapshot.Stage);
        Assert.AreEqual(
            BridgeCutoverFailureReason.NodeStillOnline,
            transaction.Snapshot.FailureReason);
        Assert.IsFalse(transaction.Snapshot.RequiresRollback);
    }

    [TestMethod]
    public void StoreHandoffRejectsUnflushedIncompatibleLiveAndInvalidEvidence()
    {
        var cases = new[]
        {
            (
                new BridgeStoreHandoffEvidence(
                    StoreFlushed: false,
                    StoreCompatible: true,
                    BridgeCutoverLeaseState.Missing),
                BridgeCutoverFailureReason.StoreNotFlushed),
            (
                new BridgeStoreHandoffEvidence(
                    StoreFlushed: true,
                    StoreCompatible: false,
                    BridgeCutoverLeaseState.Missing),
                BridgeCutoverFailureReason.StoreIncompatible),
            (
                Handoff(BridgeCutoverLeaseState.Live),
                BridgeCutoverFailureReason.ActiveOwnerLive),
            (
                Handoff(BridgeCutoverLeaseState.Invalid),
                BridgeCutoverFailureReason.ActiveOwnerInvalid),
        };

        foreach (var (evidence, expectedReason) in cases)
        {
            var transaction = AdvanceToNodeOffline();

            var result = transaction.Apply(new StoreHandoffVerifiedEvent(evidence));

            Assert.IsTrue(result.Accepted, expectedReason.ToString());
            var expectedStage = expectedReason is
                BridgeCutoverFailureReason.ActiveOwnerLive or
                BridgeCutoverFailureReason.ActiveOwnerInvalid
                ? BridgeHostCutoverStage.FailedSafe
                : BridgeHostCutoverStage.RollbackRequired;
            Assert.AreEqual(
                expectedStage,
                transaction.Snapshot.Stage,
                expectedReason.ToString());
            Assert.AreEqual(
                expectedReason,
                transaction.Snapshot.FailureReason,
                expectedReason.ToString());
            Assert.AreEqual(
                expectedStage is BridgeHostCutoverStage.RollbackRequired,
                transaction.Snapshot.RequiresRollback,
                expectedReason.ToString());
        }
    }

    [TestMethod]
    public void MissingAndStaleLeasesBothPermitTheModeledHandoff()
    {
        foreach (var leaseState in new[]
                 {
                     BridgeCutoverLeaseState.Missing,
                     BridgeCutoverLeaseState.Stale,
                 })
        {
            var transaction = AdvanceToNodeOffline();

            var result = transaction.Apply(
                new StoreHandoffVerifiedEvent(Handoff(leaseState)));

            Assert.IsTrue(result.Accepted, leaseState.ToString());
            Assert.AreEqual(
                BridgeHostCutoverStage.StoreHandoffVerified,
                transaction.Snapshot.Stage,
                leaseState.ToString());
        }
    }

    [TestMethod]
    public void LiveOrInvalidLeaseTakesPrecedenceOverStoreRecovery()
    {
        foreach (var leaseState in new[]
                 {
                     BridgeCutoverLeaseState.Live,
                     BridgeCutoverLeaseState.Invalid,
                 })
        {
            var transaction = AdvanceToNodeOffline();

            transaction.Apply(
                new StoreHandoffVerifiedEvent(
                    new BridgeStoreHandoffEvidence(
                        StoreFlushed: false,
                        StoreCompatible: false,
                        leaseState)));

            Assert.AreEqual(
                BridgeHostCutoverStage.FailedSafe,
                transaction.Snapshot.Stage,
                leaseState.ToString());
            Assert.IsFalse(
                transaction.Snapshot.RequiresRollback,
                leaseState.ToString());
        }
    }

    [TestMethod]
    public void DotNetIdentityMismatchRequiresRollback()
    {
        var transaction = AdvanceToDotNetStart();

        var result = transaction.Apply(
            new DotNetActiveVerifiedEvent(DotNet(DotNetProcessId + 1)));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(
            BridgeHostCutoverStage.RollbackRequired,
            transaction.Snapshot.Stage);
        Assert.IsTrue(transaction.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetIdentityMismatch,
            transaction.Snapshot.FailureReason);
    }

    [TestMethod]
    public void FailureAfterDotNetStartRequiresRollbackInsteadOfTerminalSuccess()
    {
        var transaction = AdvanceToDotNetStart();

        var result = transaction.Apply(
            new CutoverFailedEvent(BridgeCutoverFailureReason.UnexpectedFailure));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(
            BridgeHostCutoverStage.RollbackRequired,
            transaction.Snapshot.Stage);
        Assert.IsTrue(transaction.Snapshot.RequiresRollback);
    }

    [TestMethod]
    public void FailureDuringRollbackBecomesTerminalFailedSafe()
    {
        var transaction = AdvanceToRollbackRequired();

        var result = transaction.Apply(
            new CutoverFailedEvent(transaction.Snapshot.FailureReason));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(
            BridgeHostCutoverStage.FailedSafe,
            transaction.Snapshot.Stage);
        Assert.IsTrue(transaction.Snapshot.IsTerminal);
        Assert.IsTrue(transaction.Snapshot.RequiresRollback);
    }

    [TestMethod]
    public void RollbackMustVerifyDotNetOfflineBeforeRestartingNode()
    {
        var transaction = AdvanceToRollbackRequired();

        var earlyRestart = transaction.Apply(
            new NodeRollbackStartRequestedEvent(RollbackNodeProcessId));
        Assert.IsFalse(earlyRestart.Accepted);
        Assert.AreEqual(
            BridgeHostCutoverStage.RollbackRequired,
            transaction.Snapshot.Stage);

        AssertAdvance(
            transaction,
            new DotNetStopRequestedEvent(),
            BridgeHostCutoverStage.DotNetStopRequested);
        AssertAdvance(
            transaction,
            new DotNetOfflineVerifiedEvent(DotNetProcessId),
            BridgeHostCutoverStage.DotNetOfflineVerified);
        AssertAdvance(
            transaction,
            new NodeRollbackStartRequestedEvent(RollbackNodeProcessId),
            BridgeHostCutoverStage.NodeRollbackStartRequested);
        AssertAdvance(
            transaction,
            new NodeRollbackActiveVerifiedEvent(Node(RollbackNodeProcessId)),
            BridgeHostCutoverStage.RolledBack);

        Assert.IsTrue(transaction.Snapshot.IsTerminal);
        Assert.IsFalse(transaction.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetIdentityMismatch,
            transaction.Snapshot.FailureReason);
    }

    [TestMethod]
    public void RollbackRejectsWrongDotNetAndNodeProcessIdentities()
    {
        var dotNetMismatch = AdvanceToRollbackRequired();
        dotNetMismatch.Apply(new DotNetStopRequestedEvent());
        dotNetMismatch.Apply(new DotNetOfflineVerifiedEvent(DotNetProcessId + 1));
        Assert.AreEqual(
            BridgeHostCutoverStage.FailedSafe,
            dotNetMismatch.Snapshot.Stage);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetStillOnline,
            dotNetMismatch.Snapshot.FailureReason);
        Assert.IsTrue(dotNetMismatch.Snapshot.RequiresRollback);

        var nodeMismatch = AdvanceToRollbackRequired();
        nodeMismatch.Apply(new DotNetStopRequestedEvent());
        nodeMismatch.Apply(new DotNetOfflineVerifiedEvent(DotNetProcessId));
        nodeMismatch.Apply(new NodeRollbackStartRequestedEvent(RollbackNodeProcessId));
        nodeMismatch.Apply(
            new NodeRollbackActiveVerifiedEvent(Node(RollbackNodeProcessId + 1)));
        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, nodeMismatch.Snapshot.Stage);
        Assert.AreEqual(
            BridgeCutoverFailureReason.NodeRollbackIdentityMismatch,
            nodeMismatch.Snapshot.FailureReason);
        Assert.IsTrue(nodeMismatch.Snapshot.RequiresRollback);
    }

    [TestMethod]
    public void RepeatingCurrentEventsIsIdempotent()
    {
        var transaction = CreateTransaction();
        BridgeHostCutoverEvent[] events =
        [
            new NodeStopRequestedEvent(Node(NodeProcessId)),
            new NodeOfflineVerifiedEvent(NodeProcessId),
            new StoreHandoffVerifiedEvent(Handoff(BridgeCutoverLeaseState.Missing)),
            new DotNetStartRequestedEvent(DotNetProcessId),
            new DotNetActiveVerifiedEvent(DotNet(DotNetProcessId)),
            new CutoverCompletedEvent(),
        ];

        foreach (var @event in events)
        {
            var first = transaction.Apply(@event);
            var repeated = transaction.Apply(@event);

            Assert.IsTrue(first.Accepted, @event.GetType().Name);
            Assert.IsTrue(first.Changed, @event.GetType().Name);
            Assert.IsTrue(repeated.Accepted, @event.GetType().Name);
            Assert.IsFalse(repeated.Changed, @event.GetType().Name);
        }
    }

    [TestMethod]
    public void ModeledStagesNeverRepresentTwoActiveOwners()
    {
        var successful = CreateTransaction();
        AssertAtMostOneActiveOwner(successful.Snapshot.Stage);
        foreach (var @event in new BridgeHostCutoverEvent[]
                 {
                     new NodeStopRequestedEvent(Node(NodeProcessId)),
                     new NodeOfflineVerifiedEvent(NodeProcessId),
                     new StoreHandoffVerifiedEvent(
                         Handoff(BridgeCutoverLeaseState.Missing)),
                     new DotNetStartRequestedEvent(DotNetProcessId),
                     new DotNetActiveVerifiedEvent(DotNet(DotNetProcessId)),
                     new CutoverCompletedEvent(),
                 })
        {
            successful.Apply(@event);
            AssertAtMostOneActiveOwner(successful.Snapshot.Stage);
        }

        var rollback = AdvanceToRollbackRequired();
        AssertAtMostOneActiveOwner(rollback.Snapshot.Stage);
        foreach (var @event in new BridgeHostCutoverEvent[]
                 {
                     new DotNetStopRequestedEvent(),
                     new DotNetOfflineVerifiedEvent(DotNetProcessId),
                     new NodeRollbackStartRequestedEvent(RollbackNodeProcessId),
                     new NodeRollbackActiveVerifiedEvent(Node(RollbackNodeProcessId)),
                 })
        {
            rollback.Apply(@event);
            AssertAtMostOneActiveOwner(rollback.Snapshot.Stage);
        }
    }

    [TestMethod]
    public void SnapshotExposesOnlyCoarseTransactionState()
    {
        var propertyNames = typeof(BridgeHostCutoverSnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "FailureReason", "IsTerminal", "RequiresRollback", "Stage" },
            propertyNames);
        Assert.IsNull(typeof(BridgeHostCutoverTransaction).GetProperty("ProcessId"));
        Assert.IsNull(typeof(BridgeHostCutoverTransaction).GetProperty("Path"));
        Assert.IsNull(typeof(BridgeHostCutoverTransaction).GetProperty("Token"));
        Assert.IsNull(typeof(BridgeHostCutoverTransaction).GetProperty("LeaseId"));
    }

    private static BridgeHostCutoverTransaction CreateTransaction() =>
        BridgeHostCutoverTransaction.Create(Node(NodeProcessId), DotNetInstanceName);

    private static BridgeHostCutoverTransaction AdvanceToNodeOffline()
    {
        var transaction = CreateTransaction();
        transaction.Apply(new NodeStopRequestedEvent(Node(NodeProcessId)));
        transaction.Apply(new NodeOfflineVerifiedEvent(NodeProcessId));
        return transaction;
    }

    private static BridgeHostCutoverTransaction AdvanceToDotNetStart()
    {
        var transaction = AdvanceToNodeOffline();
        transaction.Apply(
            new StoreHandoffVerifiedEvent(Handoff(BridgeCutoverLeaseState.Missing)));
        transaction.Apply(new DotNetStartRequestedEvent(DotNetProcessId));
        return transaction;
    }

    private static BridgeHostCutoverTransaction AdvanceToRollbackRequired()
    {
        var transaction = AdvanceToDotNetStart();
        transaction.Apply(
            new DotNetActiveVerifiedEvent(DotNet(DotNetProcessId + 1)));
        return transaction;
    }

    private static BridgeStoreHandoffEvidence Handoff(BridgeCutoverLeaseState leaseState) =>
        new(StoreFlushed: true, StoreCompatible: true, leaseState);

    private static BridgeCutoverHostIdentity Node(int processId) =>
        new(
            processId,
            "node",
            BridgeHostTarget.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            "production");

    private static BridgeCutoverHostIdentity DotNet(int processId) =>
        new(
            processId,
            "dotnet",
            BridgeHostTarget.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            DotNetInstanceName);

    private static void AssertAdvance(
        BridgeHostCutoverTransaction transaction,
        BridgeHostCutoverEvent @event,
        BridgeHostCutoverStage expectedStage)
    {
        var result = transaction.Apply(@event);

        Assert.IsTrue(result.Accepted);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(expectedStage, result.Snapshot.Stage);
    }

    private static void AssertAtMostOneActiveOwner(BridgeHostCutoverStage stage)
    {
        var nodeMayBeActive = stage is
            BridgeHostCutoverStage.Planned or
            BridgeHostCutoverStage.NodeStopRequested or
            BridgeHostCutoverStage.RolledBack;
        var dotNetMayBeActive = stage is
            BridgeHostCutoverStage.DotNetActiveVerified or
            BridgeHostCutoverStage.Completed or
            BridgeHostCutoverStage.RollbackRequired or
            BridgeHostCutoverStage.DotNetStopRequested;

        Assert.IsFalse(
            nodeMayBeActive && dotNetMayBeActive,
            $"阶段 {stage} 不能同时表示 Node 与 .NET Active Owner。");
    }
}
