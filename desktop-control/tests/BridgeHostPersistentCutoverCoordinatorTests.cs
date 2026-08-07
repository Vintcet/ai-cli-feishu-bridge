using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostPersistentCutoverCoordinatorTests
{
    private const int NodeProcessId = 41001;
    private const int DotNetProcessId = 42001;
    private const int RollbackNodeProcessId = 43001;
    private const string NodeInstanceName = "production";
    private const string DotNetInstanceName = "production-dotnet";

    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-persistent-cutover-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SuccessfulCutoverPersistsEveryStageBeforeItsSideEffect()
    {
        var operations = new RecordingOperations();
        var writes = new List<BridgeHostCutoverStage>();
        var result = await Coordinator(
            operations,
            async (writer, checkpoint, cancellationToken) =>
            {
                writes.Add(checkpoint.Stage);
                operations.Calls.Add($"checkpoint.{checkpoint.Stage}");
                operations.PersistedAt.Add(checkpoint.UpdatedAt);
                return await writer.TryWriteAsync(checkpoint, cancellationToken);
            }).RunAsync(Node(NodeProcessId), DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Completed, result.State);
        Assert.IsTrue(result.Completed);
        CollectionAssert.AreEqual(
            new[]
            {
                BridgeHostCutoverStage.Planned,
                BridgeHostCutoverStage.NodeStopRequested,
                BridgeHostCutoverStage.NodeOfflineVerified,
                BridgeHostCutoverStage.StoreHandoffVerified,
                BridgeHostCutoverStage.DotNetStartRequested,
                BridgeHostCutoverStage.DotNetActiveVerified,
                BridgeHostCutoverStage.Completed,
            },
            writes);
        CollectionAssert.AreEqual(
            new[]
            {
                "checkpoint.Planned",
                "checkpoint.NodeStopRequested",
                "node.stop",
                "node.offline",
                "checkpoint.NodeOfflineVerified",
                "store.inspect",
                "checkpoint.StoreHandoffVerified",
                "dotnet.start",
                "checkpoint.DotNetStartRequested",
                "dotnet.start.bound",
                "dotnet.active",
                "checkpoint.DotNetActiveVerified",
                "checkpoint.Completed",
            },
            operations.Calls);

        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.Completed, checkpoint.Stage);
        Assert.AreEqual(DotNetProcessId, checkpoint.DotNetProcessId);
        Assert.AreEqual(0, checkpoint.NodeRollbackProcessId);
        AssertStrictlyIncreasing(operations.PersistedAt);
    }

    [TestMethod]
    public async Task DotNetIdentityFailurePersistsRollbackIntentBeforeStopsAndLaunches()
    {
        var operations = new RecordingOperations
        {
            DotNetIdentity = DotNet(DotNetProcessId + 1),
        };
        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.RolledBack, result.State);
        Assert.IsTrue(result.RolledBack);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, checkpoint.Stage);
        Assert.AreEqual(DotNetProcessId, checkpoint.DotNetProcessId);
        Assert.AreEqual(RollbackNodeProcessId, checkpoint.NodeRollbackProcessId);
        Assert.AreEqual(
            BridgeCutoverFailureReason.DotNetIdentityMismatch,
            checkpoint.FailureReason);
        AssertComesBefore(operations.Calls, "checkpoint.DotNetStopRequested", "dotnet.stop");
        AssertComesBefore(
            operations.Calls,
            "checkpoint.NodeRollbackStartRequested",
            "node.rollback.start.bound");
    }

    [TestMethod]
    public async Task StoreFailurePersistsRollbackBeforeStartingReplacementNode()
    {
        var operations = new RecordingOperations
        {
            HandoffSequence = new Queue<BridgeStoreHandoffEvidence>(new[]
            {
                new BridgeStoreHandoffEvidence(
                    StoreFlushed: false,
                    StoreCompatible: true,
                    BridgeCutoverLeaseState.Missing),
                new BridgeStoreHandoffEvidence(
                    StoreFlushed: true,
                    StoreCompatible: true,
                    BridgeCutoverLeaseState.Missing),
            }),
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.RolledBack, result.State);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(
            BridgeCutoverFailureReason.StoreNotFlushed,
            checkpoint.FailureReason);
        CollectionAssert.DoesNotContain(operations.Calls, "dotnet.start");
        AssertComesBefore(
            operations.Calls,
            "checkpoint.RollbackRequired",
            "node.rollback.start");
    }

    [TestMethod]
    public async Task UnsafeRollbackHandoffFailsSafeWithoutStartingNode()
    {
        var operations = new RecordingOperations
        {
            HandoffSequence = new Queue<BridgeStoreHandoffEvidence>(new[]
            {
                new BridgeStoreHandoffEvidence(
                    StoreFlushed: false,
                    StoreCompatible: true,
                    BridgeCutoverLeaseState.Missing),
                new BridgeStoreHandoffEvidence(
                    StoreFlushed: false,
                    StoreCompatible: true,
                    BridgeCutoverLeaseState.Missing),
            }),
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.FailedSafe, result.State);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, checkpoint.Stage);
        Assert.IsTrue(checkpoint.RequiresRollback);
        CollectionAssert.DoesNotContain(operations.Calls, "node.rollback.start");
    }

    [TestMethod]
    public async Task PersistenceFailureBeforeNodeStopStartsNoOwnershipSideEffects()
    {
        var operations = new RecordingOperations();
        var result = await Coordinator(
            operations,
            FailWriteAt(BridgeHostCutoverStage.NodeStopRequested)).RunAsync(
                Node(NodeProcessId),
                DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Unavailable, result.State);
        Assert.AreEqual(0, operations.Calls.Count);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.Planned, checkpoint.Stage);
    }

    [TestMethod]
    public async Task PersistenceFailureAfterNodeStopPreservesLastDurableCheckpointAndStops()
    {
        var operations = new RecordingOperations();
        var result = await Coordinator(
            operations,
            FailWriteAt(BridgeHostCutoverStage.NodeOfflineVerified)).RunAsync(
                Node(NodeProcessId),
                DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Unavailable, result.State);
        CollectionAssert.AreEqual(
            new[] { "node.stop", "node.offline" },
            operations.Calls);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.NodeStopRequested, checkpoint.Stage);
        Assert.IsFalse(result.DurableSnapshot!.IsTerminal);
    }

    [TestMethod]
    public async Task LaunchPidIsDurableBeforeLaunchReturnsToCoordinator()
    {
        var operations = new RecordingOperations
        {
            ThrowAfterDotNetBound = true,
        };
        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.RolledBack, result.State);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(DotNetProcessId, checkpoint.DotNetProcessId);
        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, checkpoint.Stage);
        AssertComesBefore(
            operations.Calls,
            "checkpoint.DotNetStartRequested",
            "dotnet.start.bound");
        AssertComesBefore(operations.Calls, "dotnet.start.bound", "dotnet.stop");
    }

    [TestMethod]
    public async Task DotNetLaunchWithoutCallbackFailsSafeWithoutGuessingOrStartingNode()
    {
        var operations = new RecordingOperations
        {
            SkipDotNetCallback = true,
        };
        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.FailedSafe, result.State);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.FailedSafe, checkpoint.Stage);
        Assert.AreEqual(0, checkpoint.DotNetProcessId);
        Assert.AreEqual(
            BridgeCutoverFailureReason.OwnershipUncertain,
            checkpoint.FailureReason);
        CollectionAssert.DoesNotContain(operations.Calls, "dotnet.stop");
        CollectionAssert.DoesNotContain(operations.Calls, "node.rollback.start");
    }

    [TestMethod]
    public async Task VersionConflictBeforeNodeStopStartsNoOwnershipSideEffects()
    {
        var operations = new RecordingOperations();
        var result = await Coordinator(
            operations,
            ReturnWriteStateAt(
                BridgeHostCutoverStage.NodeStopRequested,
                BridgeHostCutoverCheckpointWriteState.VersionConflict)).RunAsync(
                Node(NodeProcessId),
                DotNetInstanceName);

        Assert.AreEqual(
            BridgeHostPersistentCutoverState.CheckpointConflict,
            result.State);
        Assert.AreEqual(0, operations.Calls.Count);
        Assert.AreEqual(BridgeHostCutoverStage.Planned, (await ReadCheckpoint()).Stage);
    }

    [TestMethod]
    public async Task InvalidCheckpointConflictMapsToRecoveryRequired()
    {
        var operations = new RecordingOperations();
        var result = await Coordinator(
            operations,
            ReturnWriteStateAt(
                BridgeHostCutoverStage.NodeStopRequested,
                BridgeHostCutoverCheckpointWriteState.InvalidCurrentCheckpoint)).RunAsync(
                Node(NodeProcessId),
                DotNetInstanceName);

        Assert.AreEqual(
            BridgeHostPersistentCutoverState.CheckpointRecoveryRequired,
            result.State);
        Assert.AreEqual(0, operations.Calls.Count);
        Assert.AreEqual(BridgeHostCutoverStage.Planned, (await ReadCheckpoint()).Stage);
    }

    [TestMethod]
    public async Task WriterExceptionMapsToUnavailableAndPreservesLastDurableStage()
    {
        var operations = new RecordingOperations();
        var persist = PersistRecording(operations);
        var result = await Coordinator(
            operations,
            (writer, checkpoint, cancellationToken) => checkpoint.Stage is
                    BridgeHostCutoverStage.NodeOfflineVerified
                ? ValueTask.FromException<BridgeHostCutoverCheckpointWriteResult>(
                    new IOException("simulated disk failure"))
                : persist(writer, checkpoint, cancellationToken)).RunAsync(
                Node(NodeProcessId),
                DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Unavailable, result.State);
        Assert.AreEqual(
            BridgeHostCutoverStage.NodeStopRequested,
            (await ReadCheckpoint()).Stage);
    }

    [TestMethod]
    public async Task CompletedWriteFailureNeverClaimsCommit()
    {
        var operations = new RecordingOperations();
        var result = await Coordinator(
            operations,
            FailWriteAt(BridgeHostCutoverStage.Completed)).RunAsync(
                Node(NodeProcessId),
                DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Unavailable, result.State);
        Assert.IsFalse(result.Completed);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual(BridgeHostCutoverStage.DotNetActiveVerified, checkpoint.Stage);
    }

    [TestMethod]
    public async Task CancellationBeforePlannedWriteHasNoCheckpointOrSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operations = new RecordingOperations();

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName,
            cancellation.Token);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Cancelled, result.State);
        Assert.AreEqual(0, operations.Calls.Count);
        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Missing,
            (await Store().ReadAsync()).State);
    }

    [TestMethod]
    public async Task CancellationAfterPlannedDoesNotInterruptSafetySequence()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new RecordingOperations
        {
            CancelAfterNodeStop = cancellation,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName,
            cancellation.Token);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Completed, result.State);
        Assert.IsTrue(operations.AllSafetyTokensWereNone);
    }

    [TestMethod]
    public async Task LiveWriterReturnsBusyAndDoesNotPublishOrTouchProcesses()
    {
        Directory.CreateDirectory(directory!);
        var acquisition = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
            directory!,
            "live-operation");
        using var liveWriter = acquisition.Writer!;
        var operations = new RecordingOperations();

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Busy, result.State);
        Assert.AreEqual(0, operations.Calls.Count);
        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Missing,
            (await Store().ReadAsync()).State);
    }

    [TestMethod]
    public async Task UnfinishedCheckpointRequiresRecoveryAndIsNotOverwritten()
    {
        var existing = Checkpoint(BridgeHostCutoverStage.NodeStopRequested);
        await Store().WriteAsync(existing);
        var bytes = await File.ReadAllBytesAsync(Store().CheckpointPath);
        var operations = new RecordingOperations();

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(
            BridgeHostPersistentCutoverState.CheckpointRecoveryRequired,
            result.State);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Store().CheckpointPath));
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task PriorTerminalCheckpointAllowsANewIndependentOperation()
    {
        var existing = Checkpoint(
            BridgeHostCutoverStage.Completed,
            dotNetProcessId: DotNetProcessId);
        await Store().WriteAsync(existing);
        var operations = new RecordingOperations
        {
            ExpectedNodeProcessId = NodeProcessId + 9,
        };

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId + 9),
            DotNetInstanceName);

        Assert.AreEqual(BridgeHostPersistentCutoverState.Completed, result.State);
        var checkpoint = await ReadCheckpoint();
        Assert.AreEqual("persistent-operation", checkpoint.OperationId);
        Assert.AreEqual(NodeProcessId + 9, checkpoint.ExpectedNode.ProcessId);
        Assert.IsTrue(checkpoint.UpdatedAt > existing.UpdatedAt);
    }

    [TestMethod]
    public async Task OrphanedTemporaryFileRequiresExplicitCheckpointRecovery()
    {
        Directory.CreateDirectory(directory!);
        var orphanPath =
            $"{Store().CheckpointPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(orphanPath, "partial");
        var operations = new RecordingOperations();

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);

        Assert.AreEqual(
            BridgeHostPersistentCutoverState.CheckpointRecoveryRequired,
            result.State);
        Assert.IsTrue(File.Exists(orphanPath));
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task PublicResultAndCheckpointDoNotLeakSensitiveValues()
    {
        const string controlToken = "secret-control-token";
        const string leaseId = "secret-store-lease";
        const string businessContent = "secret-user-message";
        var operations = new RecordingOperations();

        var result = await Coordinator(operations).RunAsync(
            Node(NodeProcessId),
            DotNetInstanceName);
        var json = await File.ReadAllTextAsync(Store().CheckpointPath);
        var publicText = result.ToString();

        Assert.IsFalse(json.Contains(controlToken, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(leaseId, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(directory!, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(businessContent, StringComparison.Ordinal));
        Assert.IsFalse(publicText.Contains(NodeProcessId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(publicText.Contains(DotNetProcessId.ToString(), StringComparison.Ordinal));
        Assert.IsFalse(publicText.Contains(directory!, StringComparison.Ordinal));
    }

    private BridgeHostPersistentCutoverCoordinator Coordinator(
        RecordingOperations operations,
        BridgeHostPersistentCutoverCoordinator.WriteCheckpointAsync? write = null) =>
        new(
            directory!,
            operations,
            new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 7, 5, 0, 0, TimeSpan.Zero)),
            static () => "persistent-operation",
            write ?? PersistRecording(operations));

    private static BridgeHostPersistentCutoverCoordinator.WriteCheckpointAsync
        PersistRecording(RecordingOperations operations) =>
        async (writer, checkpoint, cancellationToken) =>
        {
            operations.Calls.Add($"checkpoint.{checkpoint.Stage}");
            operations.PersistedAt.Add(checkpoint.UpdatedAt);
            return await writer.TryWriteAsync(checkpoint, cancellationToken);
        };

    private static BridgeHostPersistentCutoverCoordinator.WriteCheckpointAsync
        FailWriteAt(BridgeHostCutoverStage failedStage) =>
        ReturnWriteStateAt(
            failedStage,
            BridgeHostCutoverCheckpointWriteState.Unavailable);

    private static BridgeHostPersistentCutoverCoordinator.WriteCheckpointAsync
        ReturnWriteStateAt(
            BridgeHostCutoverStage failedStage,
            BridgeHostCutoverCheckpointWriteState writeState) =>
        async (writer, checkpoint, cancellationToken) =>
        {
            if (checkpoint.Stage == failedStage)
            {
                return new(
                    writeState,
                    BridgeHostCutoverCheckpointReadState.Present);
            }
            return await writer.TryWriteAsync(checkpoint, cancellationToken);
        };

    private async Task<BridgeHostCutoverCheckpoint> ReadCheckpoint()
    {
        var read = await Store().ReadAsync();
        Assert.AreEqual(BridgeHostCutoverCheckpointReadState.Present, read.State);
        return read.Checkpoint!;
    }

    private BridgeHostCutoverCheckpointStore Store() => new(directory!);

    private static BridgeHostCutoverCheckpoint Checkpoint(
        BridgeHostCutoverStage stage,
        int dotNetProcessId = 0) =>
        new BridgeHostCutoverCheckpoint(
            BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
            "existing-operation",
            new DateTimeOffset(2026, 8, 7, 4, 0, 0, TimeSpan.Zero),
            stage,
            RequiresRollback: false,
            BridgeCutoverFailureReason.None,
            Node(NodeProcessId),
            DotNetInstanceName,
            dotNetProcessId,
            NodeRollbackProcessId: 0).Validate();

    private static BridgeCutoverHostIdentity Node(int processId) =>
        new(
            processId,
            "node",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            true,
            NodeInstanceName);

    private static BridgeCutoverHostIdentity DotNet(int processId) =>
        new(
            processId,
            "dotnet",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            true,
            DotNetInstanceName);

    private static void AssertComesBefore(
        IReadOnlyList<string> values,
        string before,
        string after)
    {
        var beforeIndex = values.IndexOf(before);
        var afterIndex = values.IndexOf(after);
        Assert.IsTrue(beforeIndex >= 0, before);
        Assert.IsTrue(afterIndex >= 0, after);
        Assert.IsTrue(beforeIndex < afterIndex, $"{before} should precede {after}");
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<DateTimeOffset> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            Assert.IsTrue(values[index] > values[index - 1]);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingOperations : IBridgeHostPersistentCutoverOperations
    {
        public List<string> Calls { get; } = [];

        public List<DateTimeOffset> PersistedAt { get; } = [];

        public BridgeStoreHandoffEvidence Handoff { get; set; } =
            new(true, true, BridgeCutoverLeaseState.Missing);

        public Queue<BridgeStoreHandoffEvidence>? HandoffSequence { get; set; }

        public BridgeCutoverHostIdentity DotNetIdentity { get; set; } =
            DotNet(DotNetProcessId);

        public BridgeCutoverHostIdentity RollbackNodeIdentity { get; set; } =
            Node(RollbackNodeProcessId);

        public bool ThrowAfterDotNetBound { get; set; }

        public bool SkipDotNetCallback { get; set; }

        public CancellationTokenSource? CancelAfterNodeStop { get; set; }

        public bool AllSafetyTokensWereNone { get; private set; } = true;

        public int ExpectedNodeProcessId { get; set; } = NodeProcessId;

        public ValueTask RequestNodeStopAsync(
            BridgeCutoverHostIdentity expectedNode,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Assert.IsTrue(expectedNode.Matches(Node(ExpectedNodeProcessId)));
            Calls.Add("node.stop");
            CancelAfterNodeStop?.Cancel();
            return ValueTask.CompletedTask;
        }

        public ValueTask VerifyNodeOfflineAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Assert.AreEqual(ExpectedNodeProcessId, expectedProcessId);
            Calls.Add("node.offline");
            return ValueTask.CompletedTask;
        }

        public ValueTask<BridgeStoreHandoffEvidence> InspectStoreHandoffAsync(
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Calls.Add("store.inspect");
            return ValueTask.FromResult(
                HandoffSequence is { Count: > 0 }
                    ? HandoffSequence.Dequeue()
                    : Handoff);
        }

        public ValueTask<int> StartDotNetActiveAsync(
            string instanceName,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Persistent coordinator must use callback launch.");

        public async ValueTask<int> StartDotNetActiveAndBindAsync(
            string instanceName,
            BridgeHostProcessStartedCallback processStarted,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Assert.AreEqual(DotNetInstanceName, instanceName);
            Calls.Add("dotnet.start");
            if (!SkipDotNetCallback)
            {
                await processStarted(DotNetProcessId, cancellationToken);
                Calls.Add("dotnet.start.bound");
            }
            if (ThrowAfterDotNetBound)
            {
                throw new BridgeHostCutoverOperationException(
                    BridgeCutoverFailureReason.OwnershipUncertain,
                    "simulated launch return failure");
            }
            return DotNetProcessId;
        }

        public ValueTask<BridgeCutoverHostIdentity> VerifyDotNetActiveAsync(
            int expectedProcessId,
            string expectedInstanceName,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Calls.Add("dotnet.active");
            return ValueTask.FromResult(DotNetIdentity);
        }

        public ValueTask RequestDotNetStopAsync(
            int expectedProcessId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Persistent rollback must use exact identity.");

        public ValueTask RequestExpectedDotNetStopAsync(
            BridgeCutoverHostIdentity expectedDotNet,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Assert.AreEqual(DotNetProcessId, expectedDotNet.ProcessId);
            Assert.AreEqual(DotNetInstanceName, expectedDotNet.InstanceName);
            Calls.Add("dotnet.stop");
            return ValueTask.CompletedTask;
        }

        public ValueTask VerifyDotNetOfflineAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Calls.Add("dotnet.offline");
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> StartNodeActiveAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Persistent coordinator must use callback launch.");

        public async ValueTask<int> StartNodeActiveAndBindAsync(
            BridgeHostProcessStartedCallback processStarted,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Calls.Add("node.rollback.start");
            await processStarted(RollbackNodeProcessId, cancellationToken);
            Calls.Add("node.rollback.start.bound");
            return RollbackNodeProcessId;
        }

        public ValueTask<BridgeCutoverHostIdentity> VerifyNodeActiveAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            RecordSafetyToken(cancellationToken);
            Calls.Add("node.rollback.active");
            return ValueTask.FromResult(RollbackNodeIdentity);
        }

        private void RecordSafetyToken(CancellationToken cancellationToken)
        {
            AllSafetyTokensWereNone &= cancellationToken == CancellationToken.None;
        }
    }
}

internal static class PersistentCutoverTestListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T expected)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], expected))
            {
                return index;
            }
        }
        return -1;
    }
}
