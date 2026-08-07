using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostRecoveryExecutorTests
{
    private const string NodeInstanceName = "node-main";
    private const string DotNetInstanceName = "dotnet-main";

    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-recovery-executor-{Guid.NewGuid():N}");
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
    public async Task MissingCheckpointRequiresManualInterventionWithoutObservation()
    {
        using var observer = ThrowingObserver();
        var operations = new FakeOperations();
        var executor = Executor(observer, operations);

        var result = await executor.RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.ManualIntervention,
            result.State);
        Assert.AreEqual(
            BridgeHostRecoveryReason.CheckpointMissing,
            result.Plan!.Reason);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task LiveWriterMakesRecoveryBusyWithoutObservationOrSideEffects()
    {
        await WriteCheckpoint(Checkpoint(BridgeHostCutoverStage.Planned));
        var acquired = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
            directory!,
            "operation-a");
        using var liveWriter = acquired.Writer!;
        using var observer = ThrowingObserver();
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Busy, result.State);
        Assert.IsNull(result.Plan);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task OrphanedCheckpointWriteMustBeQuarantinedBeforeExecution()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        var store = Store();
        await File.WriteAllTextAsync(
            $"{store.CheckpointPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp",
            "partial");
        using var observer = ThrowingObserver();
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.CheckpointRecoveryRequired,
            result.State);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task ManualPlanNeverRunsOperations()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Uncertain(),
            MissingLease());
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.ManualIntervention,
            result.State);
        Assert.AreEqual(
            BridgeHostRecoveryReason.EndpointUncertain,
            result.Plan!.Reason);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task StableExpectedOwnerNeedsNoRecoverySideEffects()
    {
        foreach (var (checkpoint, identity) in new[]
        {
            (Checkpoint(BridgeHostCutoverStage.Planned), Node(91001)),
            (Checkpoint(BridgeHostCutoverStage.Completed, dotNetProcessId: 91002),
                DotNet(91002)),
        })
        {
            await WriteCheckpoint(checkpoint);
            using var observer = Observer(
                BridgeHostRecoveryEndpointObservation.Authenticated(identity),
                LiveLease(identity));
            var operations = new FakeOperations();

            var result = await Executor(observer, operations).RunAsync();

            Assert.AreEqual(
                BridgeHostRecoveryExecutionState.NoActionRequired,
                result.State,
                checkpoint.Stage.ToString());
            Assert.AreEqual(0, operations.Calls.Count, checkpoint.Stage.ToString());
        }
    }

    [TestMethod]
    public async Task UnboundDotNetOwnerCannotBeStoppedFromAnEarlierCheckpoint()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        var dotNet = DotNet(91003);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Authenticated(dotNet),
            LiveLease(dotNet));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.ManualIntervention,
            result.State);
        Assert.AreEqual(
            BridgeHostRecoveryReason.RecoveryTargetUnbound,
            result.Plan!.Reason);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task OfflinePreCommitCheckpointRestartsNodeAfterFreshHandoff()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        var before = await File.ReadAllTextAsync(Store().CheckpointPath);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            Node(92001));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.RestartNode,
            result.Plan!.Disposition);
        CollectionAssert.AreEqual(
            new[] { "handoff", "start-node", "verify-node:92001", "verify-node:92001" },
            operations.Calls.ToArray());
        var persisted = await Store().ReadAsync();
        Assert.AreEqual(
            BridgeHostCutoverStage.RolledBack,
            persisted.Checkpoint!.Stage);
        Assert.AreEqual(92001, persisted.Checkpoint.NodeRollbackProcessId);
    }

    [TestMethod]
    public async Task OfflineRolledBackCheckpointRestartsNodeWithoutRewritingTerminalHistory()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.RolledBack) with
        {
            FailureReason = BridgeCutoverFailureReason.OwnershipUncertain,
            NodeRollbackProcessId = 91004,
        };
        await WriteCheckpoint(checkpoint);
        var before = await File.ReadAllBytesAsync(Store().CheckpointPath);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            Node(92001));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        CollectionAssert.AreEqual(
            before,
            await File.ReadAllBytesAsync(Store().CheckpointPath));
    }

    [TestMethod]
    public async Task OfflineCommittedCheckpointRestartsDotNetAfterFreshHandoff()
    {
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.Completed,
            dotNetProcessId: 91002);
        await WriteCheckpoint(checkpoint);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            DotNet(92002));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.RestartDotNet,
            result.Plan!.Disposition);
        CollectionAssert.AreEqual(
            new[]
            {
                "handoff",
                $"start-dotnet:{DotNetInstanceName}",
                $"verify-dotnet:92002:{DotNetInstanceName}",
                $"verify-dotnet:92002:{DotNetInstanceName}",
            },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task PreCommitDotNetOwnerIsStoppedBeforeNodeRollback()
    {
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.DotNetStartRequested,
            dotNetProcessId: 91003);
        await WriteCheckpoint(checkpoint);
        var dotNet = DotNet(91003);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Authenticated(dotNet),
            LiveLease(dotNet),
            Node(92001));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        CollectionAssert.AreEqual(
            new[]
            {
                "stop-dotnet:91003",
                "verify-dotnet-offline:91003",
                "handoff",
                "start-node",
                "verify-node:92001",
                "verify-node:92001",
            },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task PostStartLeaseMismatchFailsSafeWithoutConvergingCheckpoint()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        var recoveredNode = Node(92001);
        var replacementNode = Node(92009);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            BridgeHostRecoveryEndpointObservation.Authenticated(recoveredNode),
            LiveLease(replacementNode));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.FailedSafe, result.State);
        CollectionAssert.AreEqual(
            new[] { "handoff", "start-node", "verify-node:92001" },
            operations.Calls.ToArray());
        Assert.AreEqual(
            BridgeHostCutoverStage.Planned,
            (await Store().ReadAsync()).Checkpoint!.Stage);
    }

    [TestMethod]
    public async Task PostStartOwnerChangeFailsSafe()
    {
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.Completed,
            dotNetProcessId: 91002);
        await WriteCheckpoint(checkpoint);
        var recoveredDotNet = DotNet(92002);
        var replacementDotNet = DotNet(92009);
        using var observer = ObserverWithPostStartSamples(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            BridgeHostRecoveryEndpointObservation.Authenticated(recoveredDotNet),
            LiveLease(recoveredDotNet),
            BridgeHostRecoveryEndpointObservation.Authenticated(replacementDotNet),
            LiveLease(replacementDotNet));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.FailedSafe, result.State);
        CollectionAssert.AreEqual(
            new[]
            {
                "handoff",
                $"start-dotnet:{DotNetInstanceName}",
                $"verify-dotnet:92002:{DotNetInstanceName}",
            },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task ExactStartedPidChangeAfterStableObservationFailsSafe()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            Node(92001));
        var operations = new FakeOperations();
        operations.NodeVerificationResults.Enqueue(Node(92001));
        operations.NodeVerificationResults.Enqueue(Node(92009));

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.FailedSafe, result.State);
        CollectionAssert.AreEqual(
            new[]
            {
                "handoff",
                "start-node",
                "verify-node:92001",
                "verify-node:92001",
            },
            operations.Calls.ToArray());
        Assert.AreEqual(
            BridgeHostCutoverStage.Planned,
            (await Store().ReadAsync()).Checkpoint!.Stage);
    }

    [TestMethod]
    public async Task UnsafeFreshHandoffNeverStartsAnOwner()
    {
        await WriteCheckpoint(Checkpoint(BridgeHostCutoverStage.Planned));
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease());
        var operations = new FakeOperations
        {
            Handoff = new(
                StoreFlushed: false,
                StoreCompatible: true,
                BridgeCutoverLeaseState.Stale),
        };

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.UnsafeStoreHandoff,
            result.State);
        CollectionAssert.AreEqual(
            new[] { "handoff" },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task RollbackStopsDotNetButUnsafeHandoffPreventsNodeStart()
    {
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.DotNetStartRequested,
            dotNetProcessId: 91003);
        await WriteCheckpoint(checkpoint);
        var dotNet = DotNet(91003);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Authenticated(dotNet),
            LiveLease(dotNet));
        var operations = new FakeOperations
        {
            Handoff = new(
                StoreFlushed: false,
                StoreCompatible: true,
                BridgeCutoverLeaseState.Live),
        };

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.UnsafeStoreHandoff,
            result.State);
        CollectionAssert.AreEqual(
            new[]
            {
                "stop-dotnet:91003",
                "verify-dotnet-offline:91003",
                "handoff",
            },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task CheckpointRewriteAfterHandoffBlocksOwnerStartup()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease());
        var operations = new FakeOperations
        {
            OnInspectHandoff = () => File.WriteAllText(
                Store().CheckpointPath,
                BridgeHostCutoverCheckpointJson.Serialize(checkpoint) + " "),
        };

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.ManualIntervention,
            result.State);
        Assert.AreEqual(
            BridgeHostRecoveryReason.CheckpointChanged,
            result.Plan!.Reason);
        CollectionAssert.AreEqual(
            new[] { "handoff" },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task CheckpointWriterLockIsHeldAcrossRecoveryOperations()
    {
        await WriteCheckpoint(Checkpoint(BridgeHostCutoverStage.Planned));
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease(),
            Node(92001));
        var operations = new FakeOperations();
        operations.OnStartNode = async () =>
        {
            var acquisition = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
                directory!,
                "operation-a");
            Assert.AreEqual(
                BridgeHostCutoverCheckpointWriterAcquireState.Busy,
                acquisition.State);
        };

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        var after = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
            directory!,
            "operation-a");
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriterAcquireState.Acquired,
            after.State);
        after.Writer!.Dispose();
    }

    [TestMethod]
    public async Task OperationFailureReturnsFailedSafeAndReleasesWriterLock()
    {
        await WriteCheckpoint(Checkpoint(BridgeHostCutoverStage.Planned));
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Offline(),
            MissingLease());
        var operations = new FakeOperations
        {
            Failure = new InvalidOperationException("start failed"),
            FailAt = "start-node",
        };

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(BridgeHostRecoveryExecutionState.FailedSafe, result.State);
        var after = await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
            directory!,
            "operation-a");
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriterAcquireState.Acquired,
            after.State);
        after.Writer!.Dispose();
    }

    [TestMethod]
    public async Task CancellationAfterRollbackStartsDoesNotAbandonOwnershipRecovery()
    {
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.DotNetStartRequested,
            dotNetProcessId: 91003);
        await WriteCheckpoint(checkpoint);
        var dotNet = DotNet(91003);
        using var observer = Observer(
            BridgeHostRecoveryEndpointObservation.Authenticated(dotNet),
            LiveLease(dotNet),
            Node(92001));
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeOperations
        {
            OnStopDotNet = cancellation.Cancel,
        };

        var result = await Executor(observer, operations).RunAsync(cancellation.Token);

        Assert.AreEqual(BridgeHostRecoveryExecutionState.Recovered, result.State);
        CollectionAssert.AreEqual(
            new[]
            {
                "stop-dotnet:91003",
                "verify-dotnet-offline:91003",
                "handoff",
                "start-node",
                "verify-node:92001",
                "verify-node:92001",
            },
            operations.Calls.ToArray());
    }

    [TestMethod]
    public async Task CancellationBeforeExecutionHasNoSideEffects()
    {
        await WriteCheckpoint(Checkpoint(BridgeHostCutoverStage.Planned));
        using var observer = ThrowingObserver();
        var operations = new FakeOperations();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await Executor(observer, operations).RunAsync(cancellation.Token));

        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public async Task ObserverDetectedCheckpointRewriteBlocksExecution()
    {
        var checkpoint = Checkpoint(BridgeHostCutoverStage.Planned);
        await WriteCheckpoint(checkpoint);
        var store = Store();
        var rewritten = false;
        using var observer = new BridgeHostRecoveryObserver(
            store.ReadAsync,
            async cancellationToken =>
            {
                if (!rewritten)
                {
                    rewritten = true;
                    await File.WriteAllTextAsync(
                        store.CheckpointPath,
                        BridgeHostCutoverCheckpointJson.Serialize(checkpoint) + " ",
                        cancellationToken);
                }
                return BridgeHostRecoveryEndpointObservation.Offline();
            },
            _ => ValueTask.FromResult(MissingLease()));
        var operations = new FakeOperations();

        var result = await Executor(observer, operations).RunAsync();

        Assert.AreEqual(
            BridgeHostRecoveryExecutionState.ManualIntervention,
            result.State);
        Assert.AreEqual(
            BridgeHostRecoveryReason.CheckpointChanged,
            result.Plan!.Reason);
        Assert.AreEqual(0, operations.Calls.Count);
    }

    [TestMethod]
    public void PublishedExecutionResultContainsNoIdentityOrPathDetails()
    {
        CollectionAssert.AreEquivalent(
            new[] { "Plan", "State" },
            typeof(BridgeHostRecoveryExecutionResult)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    private BridgeHostRecoveryExecutor Executor(
        BridgeHostRecoveryObserver observer,
        IBridgeHostRecoveryOperations operations) =>
        new(directory!, observer, operations);

    private BridgeHostRecoveryObserver Observer(
        BridgeHostRecoveryEndpointObservation endpoint,
        ActiveOwnerLeaseSnapshot lease,
        BridgeCutoverHostIdentity? recoveredOwner = null) =>
        recoveredOwner is null
            ? ObserverWithPostStartSamples(endpoint, lease)
            : Observer(
                endpoint,
                lease,
                BridgeHostRecoveryEndpointObservation.Authenticated(recoveredOwner),
                LiveLease(recoveredOwner));

    private BridgeHostRecoveryObserver Observer(
        BridgeHostRecoveryEndpointObservation endpoint,
        ActiveOwnerLeaseSnapshot lease,
        BridgeHostRecoveryEndpointObservation recoveredEndpoint,
        ActiveOwnerLeaseSnapshot recoveredLease) =>
        ObserverWithPostStartSamples(
            endpoint,
            lease,
            recoveredEndpoint,
            recoveredLease,
            recoveredEndpoint,
            recoveredLease);

    private BridgeHostRecoveryObserver ObserverWithPostStartSamples(
        BridgeHostRecoveryEndpointObservation endpoint,
        ActiveOwnerLeaseSnapshot lease,
        BridgeHostRecoveryEndpointObservation? recoveredEndpoint = null,
        ActiveOwnerLeaseSnapshot? recoveredLease = null,
        BridgeHostRecoveryEndpointObservation? recoveredEndpointAfter = null,
        ActiveOwnerLeaseSnapshot? recoveredLeaseAfter = null)
    {
        var endpoints = new Queue<BridgeHostRecoveryEndpointObservation>(
            new[] { endpoint, endpoint });
        var leases = new Queue<ActiveOwnerLeaseSnapshot>(
            new[] { lease, lease });
        if (recoveredEndpoint is not null &&
            recoveredLease is not null &&
            recoveredEndpointAfter is not null &&
            recoveredLeaseAfter is not null)
        {
            endpoints.Enqueue(recoveredEndpoint);
            endpoints.Enqueue(recoveredEndpointAfter);
            leases.Enqueue(recoveredLease);
            leases.Enqueue(recoveredLeaseAfter);
        }

        var store = Store();
        return new(
            store.ReadAsync,
            _ => ValueTask.FromResult(endpoints.Dequeue()),
            _ => ValueTask.FromResult(leases.Dequeue()));
    }

    private BridgeHostRecoveryObserver ThrowingObserver() =>
        new(
            _ => throw new InvalidOperationException("checkpoint must not be observed"),
            _ => throw new InvalidOperationException("endpoint must not be observed"),
            _ => throw new InvalidOperationException("lease must not be observed"));

    private BridgeHostCutoverCheckpointStore Store() => new(directory!);

    private async Task WriteCheckpoint(BridgeHostCutoverCheckpoint checkpoint)
    {
        Directory.CreateDirectory(directory!);
        await Store().WriteAsync(checkpoint);
    }

    private static BridgeHostCutoverCheckpoint Checkpoint(
        BridgeHostCutoverStage stage,
        int dotNetProcessId = 0) =>
        new(
            BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
            "operation-a",
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"),
            stage,
            RequiresRollback: false,
            BridgeCutoverFailureReason.None,
            Node(91001),
            DotNetInstanceName,
            dotNetProcessId,
            NodeRollbackProcessId: 0);

    private static BridgeCutoverHostIdentity Node(int processId) =>
        new(
            processId,
            "node",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            NodeInstanceName);

    private static BridgeCutoverHostIdentity DotNet(int processId) =>
        new(
            processId,
            "dotnet",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            DotNetInstanceName);

    private static ActiveOwnerLeaseSnapshot MissingLease() =>
        new(ActiveOwnerLeaseState.Missing);

    private static ActiveOwnerLeaseSnapshot LiveLease(
        BridgeCutoverHostIdentity identity) =>
        new(
            ActiveOwnerLeaseState.Live,
            new ActiveOwnerLeaseRecord(
                ActiveOwnerLeaseObserver.SchemaVersion,
                identity.HostKind,
                identity.OwnershipMode,
                identity.ProcessId,
                identity.InstanceName,
                "live-lease",
                DateTimeOffset.Parse("2026-08-07T12:00:00.000Z")));

    private sealed class FakeOperations : IBridgeHostRecoveryOperations
    {
        public List<string> Calls { get; } = [];

        public Queue<BridgeCutoverHostIdentity> NodeVerificationResults { get; } = [];

        public Queue<BridgeCutoverHostIdentity> DotNetVerificationResults { get; } = [];

        public BridgeStoreHandoffEvidence Handoff { get; set; } =
            new(
                StoreFlushed: true,
                StoreCompatible: true,
                BridgeCutoverLeaseState.Missing);

        public Exception? Failure { get; set; }

        public string? FailAt { get; set; }

        public Func<Task>? OnStartNode { get; set; }

        public Action? OnInspectHandoff { get; set; }

        public Action? OnStopDotNet { get; set; }

        public ValueTask RequestNodeStopAsync(
            BridgeCutoverHostIdentity expectedNode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must never stop Node.");

        public ValueTask VerifyNodeOfflineAsync(
            int expectedProcessId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery does not verify old Node PID.");

        public ValueTask<BridgeStoreHandoffEvidence> InspectStoreHandoffAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("handoff");
            OnInspectHandoff?.Invoke();
            ThrowIf("handoff");
            return ValueTask.FromResult(Handoff);
        }

        public ValueTask<int> StartDotNetActiveAsync(
            string instanceName,
            CancellationToken cancellationToken)
        {
            Calls.Add($"start-dotnet:{instanceName}");
            ThrowIf("start-dotnet");
            return ValueTask.FromResult(92002);
        }

        public ValueTask<BridgeCutoverHostIdentity> VerifyDotNetActiveAsync(
            int expectedProcessId,
            string expectedInstanceName,
            CancellationToken cancellationToken)
        {
            Calls.Add($"verify-dotnet:{expectedProcessId}:{expectedInstanceName}");
            ThrowIf("verify-dotnet");
            return ValueTask.FromResult(
                DotNetVerificationResults.Count > 0
                    ? DotNetVerificationResults.Dequeue()
                    : DotNet(expectedProcessId));
        }

        public ValueTask RequestDotNetStopAsync(
            int expectedProcessId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Recovery must stop .NET with a complete expected identity.");

        public ValueTask RequestExpectedDotNetStopAsync(
            BridgeCutoverHostIdentity expectedDotNet,
            CancellationToken cancellationToken)
        {
            Calls.Add($"stop-dotnet:{expectedDotNet.ProcessId}");
            Assert.AreEqual(DotNet(expectedDotNet.ProcessId), expectedDotNet);
            OnStopDotNet?.Invoke();
            ThrowIf("stop-dotnet");
            return ValueTask.CompletedTask;
        }

        public ValueTask VerifyDotNetOfflineAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            Calls.Add($"verify-dotnet-offline:{expectedProcessId}");
            ThrowIf("verify-dotnet-offline");
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> StartNodeActiveAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("start-node");
            if (OnStartNode is not null)
            {
                await OnStartNode();
            }
            ThrowIf("start-node");
            return 92001;
        }

        public ValueTask<BridgeCutoverHostIdentity> VerifyNodeActiveAsync(
            int expectedProcessId,
            CancellationToken cancellationToken)
        {
            Calls.Add($"verify-node:{expectedProcessId}");
            ThrowIf("verify-node");
            return ValueTask.FromResult(
                NodeVerificationResults.Count > 0
                    ? NodeVerificationResults.Dequeue()
                    : Node(expectedProcessId));
        }

        private void ThrowIf(string operation)
        {
            if (string.Equals(FailAt, operation, StringComparison.Ordinal))
            {
                throw Failure ?? new InvalidOperationException(operation);
            }
        }
    }
}
