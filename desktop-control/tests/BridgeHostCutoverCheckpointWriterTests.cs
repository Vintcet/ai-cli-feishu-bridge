using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostCutoverCheckpointWriterTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-checkpoint-writer-{Guid.NewGuid():N}");
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
    public async Task WriterLockAllowsOnlyOneLiveWriterAndIsReusableAfterRelease()
    {
        var firstResult = await Acquire("operation-a");
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriterAcquireState.Acquired,
            firstResult.State);
        using var first = firstResult.Writer!;

        var secondResult = await Acquire("operation-b");
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriterAcquireState.Busy,
            secondResult.State);
        Assert.IsNull(secondResult.Writer);

        first.Dispose();
        var thirdResult = await Acquire("operation-c");
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriterAcquireState.Acquired,
            thirdResult.State);
        using var third = thirdResult.Writer!;

        var lockPath = Path.Combine(
            directory!,
            BridgeHostCutoverCheckpointWriter.WriterLockFileName);
        Assert.IsTrue(File.Exists(lockPath));
        Assert.AreEqual(0, new FileInfo(lockPath).Length);
    }

    [TestMethod]
    public async Task InvalidOperationIdDoesNotCreateTheDataDirectory()
    {
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
                directory!,
                "../outside"));

        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task FirstWriteMustStartAtThePlannedStageAndBindTheOperationId()
    {
        using var writer = (await Acquire("operation-a")).Writer!;
        var planned = Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-a",
            seconds: 1);

        var written = await writer.TryWriteAsync(planned);

        Assert.AreEqual(BridgeHostCutoverCheckpointWriteState.Written, written.State);
        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Missing,
            written.CurrentCheckpointState);
        Assert.AreEqual(planned, (await Store().ReadAsync()).Checkpoint);

        var wrongOperation = Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-b",
            seconds: 2);
        var conflict = await writer.TryWriteAsync(wrongOperation);

        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriteState.OperationConflict,
            conflict.State);
        Assert.AreEqual(planned, (await Store().ReadAsync()).Checkpoint);
    }

    [TestMethod]
    public async Task ActiveOperationCannotBeOverwrittenByAStaleOperation()
    {
        using (var first = (await Acquire("operation-a")).Writer!)
        {
            await first.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.Planned,
                "operation-a",
                seconds: 1));
            await first.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.NodeStopRequested,
                "operation-a",
                seconds: 2));
        }

        using var second = (await Acquire("operation-b")).Writer!;
        var result = await second.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-b",
            seconds: 3));

        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriteState.OperationConflict,
            result.State);
        Assert.AreEqual(
            "operation-a",
            (await Store().ReadAsync()).Checkpoint?.OperationId);
    }

    [TestMethod]
    public async Task ANewOperationCanStartOnlyAfterACompletedOrRolledBackOperation()
    {
        using (var first = (await Acquire("operation-a")).Writer!)
        {
            await WriteSuccessfulCutoverAsync(first, "operation-a");
        }

        using var second = (await Acquire("operation-b")).Writer!;
        var planned = Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-b",
            seconds: 20,
            nodeProcessId: 84020);
        var result = await second.TryWriteAsync(planned);

        Assert.AreEqual(BridgeHostCutoverCheckpointWriteState.Written, result.State);
        Assert.AreEqual(planned, (await Store().ReadAsync()).Checkpoint);
    }

    [TestMethod]
    public async Task AFailedSafeRollbackCannotBeForgottenByStartingANewOperation()
    {
        using (var first = (await Acquire("operation-a")).Writer!)
        {
            await first.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.Planned,
                "operation-a",
                seconds: 1));
            await first.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.NodeStopRequested,
                "operation-a",
                seconds: 2));
            await first.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.FailedSafe,
                "operation-a",
                seconds: 3));
        }

        using var second = (await Acquire("operation-b")).Writer!;
        var result = await second.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-b",
            seconds: 4));

        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriteState.OperationConflict,
            result.State);
        Assert.AreEqual(
            BridgeHostCutoverStage.FailedSafe,
            (await Store().ReadAsync()).Checkpoint?.Stage);
    }

    [TestMethod]
    public async Task SameCheckpointIsIdempotentButTransitionsNeedMonotonicTimeAndAnAllowedEdge()
    {
        using var writer = (await Acquire("operation-a")).Writer!;
        var planned = Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-a",
            seconds: 10);
        await writer.TryWriteAsync(planned);

        var unchanged = await writer.TryWriteAsync(planned);
        Assert.AreEqual(BridgeHostCutoverCheckpointWriteState.Unchanged, unchanged.State);

        var sameTime = await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.NodeStopRequested,
            "operation-a",
            seconds: 10));
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriteState.OperationConflict,
            sameTime.State);

        var outOfOrder = await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.DotNetStartRequested,
            "operation-a",
            seconds: 11,
            dotNetProcessId: 84011));
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriteState.OperationConflict,
            outOfOrder.State);
        Assert.AreEqual(planned, (await Store().ReadAsync()).Checkpoint);
    }

    [TestMethod]
    public async Task EarlyRollbackTransitionDoesNotInventADotNetProcess()
    {
        using var writer = (await Acquire("operation-a")).Writer!;
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-a",
            seconds: 1));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.NodeStopRequested,
            "operation-a",
            seconds: 2));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.NodeOfflineVerified,
            "operation-a",
            seconds: 3));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.RollbackRequired,
            "operation-a",
            seconds: 4,
            failureReason: BridgeCutoverFailureReason.StoreNotFlushed));
        var started = await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.NodeRollbackStartRequested,
            "operation-a",
            seconds: 5,
            failureReason: BridgeCutoverFailureReason.StoreNotFlushed,
            nodeRollbackProcessId: 84015));

        Assert.AreEqual(BridgeHostCutoverCheckpointWriteState.Written, started.State);
        Assert.AreEqual(
            0,
            (await Store().ReadAsync()).Checkpoint?.DotNetProcessId);
    }

    [TestMethod]
    public async Task InvalidOrUnavailableCurrentCheckpointIsNeverOverwritten()
    {
        using var writer = (await Acquire("operation-a")).Writer!;
        var store = Store();
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(store.CheckpointPath, "{");

        var invalid = await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-a",
            seconds: 1));
        Assert.AreEqual(
            BridgeHostCutoverCheckpointWriteState.InvalidCurrentCheckpoint,
            invalid.State);
        Assert.AreEqual("{", await File.ReadAllTextAsync(store.CheckpointPath));

        await store.WriteAsync(Checkpoint(
            BridgeHostCutoverStage.Planned,
            "operation-a",
            seconds: 2));
        var before = await File.ReadAllTextAsync(store.CheckpointPath);
        await using (var locked = new FileStream(
                         store.CheckpointPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            var unavailable = await writer.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.NodeStopRequested,
                "operation-a",
                seconds: 3));
            Assert.AreEqual(
                BridgeHostCutoverCheckpointWriteState.Unavailable,
                unavailable.State);
        }
        Assert.AreEqual(before, await File.ReadAllTextAsync(store.CheckpointPath));
    }

    [TestMethod]
    public async Task DisposedWriterCannotWriteAgain()
    {
        var acquired = await Acquire("operation-a");
        var writer = acquired.Writer!;
        writer.Dispose();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
            await writer.TryWriteAsync(Checkpoint(
                BridgeHostCutoverStage.Planned,
                "operation-a",
                seconds: 1)));
    }

    [TestMethod]
    public async Task PublishedWriterResultsContainNoPathsOrProcessDetails()
    {
        CollectionAssert.AreEquivalent(
            new[] { "State", "Writer" },
            typeof(BridgeHostCutoverCheckpointWriterAcquireResult)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "State", "CurrentCheckpointState" },
            typeof(BridgeHostCutoverCheckpointWriteResult)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());

        var acquired = await Acquire("operation-a");
        using var writer = acquired.Writer!;
        var lockPath = Path.Combine(
            directory!,
            BridgeHostCutoverCheckpointWriter.WriterLockFileName);
        Assert.AreEqual(0, new FileInfo(lockPath).Length);
    }

    private async ValueTask<BridgeHostCutoverCheckpointWriterAcquireResult> Acquire(
        string operationId) =>
        await BridgeHostCutoverCheckpointWriter.TryAcquireAsync(
            directory!,
            operationId);

    private BridgeHostCutoverCheckpointStore Store() =>
        new(directory!);

    private static async Task WriteSuccessfulCutoverAsync(
        BridgeHostCutoverCheckpointWriter writer,
        string operationId)
    {
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.Planned,
            operationId,
            seconds: 1));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.NodeStopRequested,
            operationId,
            seconds: 2));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.NodeOfflineVerified,
            operationId,
            seconds: 3));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.StoreHandoffVerified,
            operationId,
            seconds: 4));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.DotNetStartRequested,
            operationId,
            seconds: 5,
            dotNetProcessId: 84005));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.DotNetActiveVerified,
            operationId,
            seconds: 6,
            dotNetProcessId: 84005));
        await writer.TryWriteAsync(Checkpoint(
            BridgeHostCutoverStage.Completed,
            operationId,
            seconds: 7,
            dotNetProcessId: 84005));
    }

    private static BridgeHostCutoverCheckpoint Checkpoint(
        BridgeHostCutoverStage stage,
        string operationId,
        int seconds,
        int nodeProcessId = 84000,
        int dotNetProcessId = 0,
        int nodeRollbackProcessId = 0,
        BridgeCutoverFailureReason? failureReason = null) =>
        new BridgeHostCutoverCheckpoint(
            BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
            operationId,
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z")
                .AddSeconds(seconds),
            stage,
            RequiresRollback(stage),
            failureReason ?? FailureReason(stage),
            Node(nodeProcessId),
            "production-dotnet",
            dotNetProcessId,
            nodeRollbackProcessId).Validate();

    private static bool RequiresRollback(BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.RollbackRequired or
            BridgeHostCutoverStage.DotNetStopRequested or
            BridgeHostCutoverStage.DotNetOfflineVerified or
            BridgeHostCutoverStage.NodeRollbackStartRequested or
            BridgeHostCutoverStage.FailedSafe;

    private static BridgeCutoverFailureReason FailureReason(
        BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.Planned or
            BridgeHostCutoverStage.NodeStopRequested or
            BridgeHostCutoverStage.NodeOfflineVerified or
            BridgeHostCutoverStage.StoreHandoffVerified or
            BridgeHostCutoverStage.DotNetStartRequested or
            BridgeHostCutoverStage.DotNetActiveVerified or
            BridgeHostCutoverStage.Completed
            ? BridgeCutoverFailureReason.None
            : BridgeCutoverFailureReason.OwnershipUncertain;

    private static BridgeCutoverHostIdentity Node(int processId) =>
        new(
            processId,
            "node",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            true,
            "production");
}
