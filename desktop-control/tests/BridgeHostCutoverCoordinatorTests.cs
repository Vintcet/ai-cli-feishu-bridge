using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostCutoverCoordinatorTests
{
    private const int NodeProcessId = 400;
    private const int DotNetProcessId = 500;
    private const int RollbackNodeProcessId = 600;
    private const string DotNetInstanceName = "production-dotnet";

    [TestMethod]
    public async Task SuccessfulCutoverUsesTheRequiredOrder()
    {
        var operations = new RecordingCutoverOperations
        {
            DotNetIdentity = DotNet(DotNetProcessId),
            RollbackNodeIdentity = Node(RollbackNodeProcessId),
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.Completed, result.Snapshot.Stage);
        Assert.IsTrue(result.Completed);
        Assert.IsFalse(result.RolledBack);
        AssertSequence(
            [
                "node.stop",
                "node.offline",
                "store.inspect",
                "store.handoff",
                "dotnet.start",
                "dotnet.active",
            ],
            operations.Calls);
    }

    [TestMethod]
    public async Task LiveLeaseStopsWithoutStartingAnotherOwner()
    {
        var operations = new RecordingCutoverOperations
        {
            Handoff = new BridgeStoreHandoffEvidence(
                StoreFlushed: true,
                StoreCompatible: true,
                BridgeCutoverLeaseState.Live),
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.AreEqual(
            BridgeCutoverFailureReason.ActiveOwnerLive,
            result.Snapshot.FailureReason);
        Assert.IsFalse(result.Snapshot.RequiresRollback);
        CollectionAssert.DoesNotContain(operations.Calls.ToArray(), "dotnet.start");
        CollectionAssert.DoesNotContain(
            operations.Calls.ToArray(),
            "node.rollback.start");
    }

    [TestMethod]
    public async Task DotNetIdentityFailureRollsBackToVerifiedNode()
    {
        var operations = new RecordingCutoverOperations
        {
            DotNetIdentity = DotNet(DotNetProcessId + 1),
            RollbackNodeIdentity = Node(RollbackNodeProcessId),
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, result.Snapshot.Stage);
        Assert.IsTrue(result.RolledBack);
        Assert.IsFalse(result.Completed);
        Assert.IsFalse(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetIdentityMismatch,
            result.Snapshot.FailureReason);
        AssertSequence(
            [
                "node.stop",
                "node.offline",
                "store.inspect",
                "store.handoff",
                "dotnet.start",
                "dotnet.active",
                "dotnet.stop",
                "dotnet.offline",
                "node.rollback.start",
                "node.rollback.active",
            ],
            operations.Calls);
    }

    [TestMethod]
    public async Task CallerCancellationAfterNodeStopCannotInterruptTheSafetySequence()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new RecordingCutoverOperations
        {
            RollbackNodeIdentity = Node(RollbackNodeProcessId),
            CancelAfterNodeStop = cancellation,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName,
            cancellation.Token);

        Assert.AreEqual(BridgeHostCutoverStage.Completed, result.Snapshot.Stage);
        Assert.IsTrue(result.Completed);
        Assert.IsTrue(operations.SafetySequenceIgnoredCallerCancellation);
    }

    [TestMethod]
    public async Task CallerCancellationBeforeNodeStopDoesNotStartCutover()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operations = new RecordingCutoverOperations();

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName,
            cancellation.Token);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsFalse(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.Cancelled,
            result.Snapshot.FailureReason);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task AmbiguousNodeStopFailureDoesNotStartEitherOwner()
    {
        var operations = new RecordingCutoverOperations
        {
            FailNodeStop = true,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsTrue(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.OwnershipUncertain,
            result.Snapshot.FailureReason);
        AssertSequence(["node.stop"], operations.Calls);
    }

    [TestMethod]
    public async Task RollbackFailureRemainsSafeAndRequestsFurtherRecovery()
    {
        var operations = new RecordingCutoverOperations
        {
            DotNetIdentity = DotNet(DotNetProcessId + 1),
            FailNodeRollbackVerification = true,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsTrue(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.NodeRollbackIdentityMismatch,
            result.Snapshot.FailureReason);
    }

    [TestMethod]
    public async Task OperationFailureAfterNodeOfflineRestoresNode()
    {
        var operations = new RecordingCutoverOperations
        {
            FailStoreInspection = true,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, result.Snapshot.Stage);
        Assert.IsFalse(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.StoreNotFlushed,
            result.Snapshot.FailureReason);
        AssertSequence(
            [
                "node.stop",
                "node.offline",
                "store.inspect",
                "node.rollback.start",
                "node.rollback.active",
            ],
            operations.Calls);
    }

    [TestMethod]
    public async Task DotNetStartFailureDoesNotGuessThatNodeCanBeRestarted()
    {
        var operations = new RecordingCutoverOperations
        {
            FailDotNetStart = true,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsTrue(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.OwnershipUncertain,
            result.Snapshot.FailureReason);
        CollectionAssert.DoesNotContain(
            operations.Calls.ToArray(),
            "node.rollback.start");
    }

    [TestMethod]
    public async Task TypedDotNetStartFailureStillDoesNotRestartNode()
    {
        var operations = new RecordingCutoverOperations
        {
            DotNetStartFailureReason = BridgeCutoverFailureReason.StoreNotFlushed,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsTrue(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.OwnershipUncertain,
            result.Snapshot.FailureReason);
        CollectionAssert.DoesNotContain(
            operations.Calls.ToArray(),
            "node.rollback.start");
    }

    [TestMethod]
    public async Task InvalidDotNetStartPidStopsWithoutRestartingNode()
    {
        var operations = new RecordingCutoverOperations
        {
            DotNetProcessId = 0,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsTrue(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetStartInvalidProcess,
            result.Snapshot.FailureReason);
        CollectionAssert.DoesNotContain(
            operations.Calls.ToArray(),
            "node.rollback.start");
    }

    [TestMethod]
    public async Task RollbackOperationFailureAlwaysReturnsTerminalSafeState()
    {
        var operations = new RecordingCutoverOperations
        {
            FailStoreInspection = true,
            NodeRollbackStartFailureReason =
                BridgeCutoverFailureReason.StoreNotFlushed,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, result.Snapshot.Stage);
        Assert.IsTrue(result.Snapshot.IsTerminal);
        Assert.IsTrue(result.Snapshot.RequiresRollback);
        Assert.AreEqual(
            BridgeCutoverFailureReason.StoreNotFlushed,
            result.Snapshot.FailureReason);
    }

    [TestMethod]
    public async Task ConcurrentRunsDoNotShareStartedProcessIds()
    {
        var first = new RecordingCutoverOperations
        {
            DotNetProcessId = 501,
            DotNetIdentity = DotNet(501),
            RollbackNodeIdentity = Node(601),
        };
        var second = new RecordingCutoverOperations
        {
            DotNetProcessId = 502,
            DotNetIdentity = DotNet(502),
            RollbackNodeIdentity = Node(602),
        };

        var results = await Task.WhenAll(
            Coordinator(first).RunAsync(Node(401), DotNetInstanceName).AsTask(),
            Coordinator(second).RunAsync(Node(402), DotNetInstanceName).AsTask());

        Assert.IsTrue(results.All(result => result.Completed));
        Assert.IsTrue(first.Calls.Contains("dotnet.active"));
        Assert.IsTrue(second.Calls.Contains("dotnet.active"));
    }

    private static BridgeHostCutoverCoordinator Coordinator(
        RecordingCutoverOperations operations) =>
        new(operations);

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

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual) =>
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());

    private sealed class RecordingCutoverOperations : IBridgeHostCutoverOperations
    {
        public List<string> Calls { get; } = [];

        public BridgeStoreHandoffEvidence Handoff { get; set; } =
            new(true, true, BridgeCutoverLeaseState.Missing);

        public int DotNetProcessId { get; set; } =
            BridgeHostCutoverCoordinatorTests.DotNetProcessId;

        public BridgeCutoverHostIdentity DotNetIdentity { get; set; } =
            DotNet(BridgeHostCutoverCoordinatorTests.DotNetProcessId);

        public BridgeCutoverHostIdentity RollbackNodeIdentity { get; set; } =
            Node(RollbackNodeProcessId);

        public bool FailStoreInspection { get; set; }

        public bool FailNodeStop { get; set; }

        public bool FailDotNetStart { get; set; }

        public BridgeCutoverFailureReason? DotNetStartFailureReason { get; set; }

        public BridgeCutoverFailureReason? NodeRollbackStartFailureReason { get; set; }

        public bool FailNodeRollbackVerification { get; set; }

        public CancellationTokenSource? CancelAfterNodeStop { get; set; }

        public bool SafetySequenceIgnoredCallerCancellation { get; private set; }

        public ValueTask RequestNodeStopAsync(
            BridgeCutoverHostIdentity expectedNode,
            CancellationToken cancellationToken)
        {
            _ = expectedNode;
            Assert.AreEqual(CancellationToken.None, cancellationToken);
            Calls.Add("node.stop");
            CancelAfterNodeStop?.Cancel();
            if (FailNodeStop)
            {
                throw new InvalidOperationException("test stop ambiguity");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask VerifyNodeOfflineAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            _ = expectedProcessId;
            if (cancellationToken == CancellationToken.None)
            {
                SafetySequenceIgnoredCallerCancellation = true;
            }
            Calls.Add("node.offline");
            return ValueTask.CompletedTask;
        }

        public ValueTask<BridgeStoreHandoffEvidence> InspectStoreHandoffAsync(
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(CancellationToken.None, cancellationToken);
            Calls.Add("store.inspect");
            if (FailStoreInspection)
            {
                throw new BridgeHostCutoverOperationException(
                    BridgeCutoverFailureReason.StoreNotFlushed,
                    "test store failure");
            }
            Calls.Add("store.handoff");
            return ValueTask.FromResult(Handoff);
        }

        public ValueTask<int> StartDotNetActiveAsync(
            string instanceName,
            CancellationToken cancellationToken)
        {
            _ = instanceName;
            Assert.AreEqual(CancellationToken.None, cancellationToken);
            Calls.Add("dotnet.start");
            if (FailDotNetStart)
            {
                throw new InvalidOperationException("test start ambiguity");
            }
            if (DotNetStartFailureReason is { } failureReason)
            {
                throw new BridgeHostCutoverOperationException(
                    failureReason,
                    "test typed start failure");
            }
            return ValueTask.FromResult(DotNetProcessId);
        }

        public ValueTask<BridgeCutoverHostIdentity> VerifyDotNetActiveAsync(
            int expectedProcessId,
            string expectedInstanceName,
            CancellationToken cancellationToken)
        {
            _ = expectedProcessId;
            _ = expectedInstanceName;
            Calls.Add("dotnet.active");
            Assert.AreEqual(CancellationToken.None, cancellationToken);
            return ValueTask.FromResult(DotNetIdentity);
        }

        public ValueTask RequestDotNetStopAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            _ = expectedProcessId;
            if (cancellationToken == CancellationToken.None)
            {
                SafetySequenceIgnoredCallerCancellation = true;
            }
            Calls.Add("dotnet.stop");
            return ValueTask.CompletedTask;
        }

        public ValueTask VerifyDotNetOfflineAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            _ = expectedProcessId;
            _ = cancellationToken;
            Calls.Add("dotnet.offline");
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> StartNodeActiveAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Calls.Add("node.rollback.start");
            if (NodeRollbackStartFailureReason is { } failureReason)
            {
                throw new BridgeHostCutoverOperationException(
                    failureReason,
                    "test rollback start failure");
            }
            return ValueTask.FromResult(RollbackNodeIdentity.ProcessId);
        }

        public ValueTask<BridgeCutoverHostIdentity> VerifyNodeActiveAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            _ = expectedProcessId;
            _ = cancellationToken;
            Calls.Add("node.rollback.active");
            if (FailNodeRollbackVerification)
            {
                return ValueTask.FromResult(
                    RollbackNodeIdentity with
                    {
                        ProcessId = RollbackNodeIdentity.ProcessId + 1,
                    });
            }
            return ValueTask.FromResult(RollbackNodeIdentity);
        }
    }
}
