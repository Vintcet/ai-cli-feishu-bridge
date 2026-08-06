using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostCutoverCheckpointTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-cutover-checkpoint-{Guid.NewGuid():N}");
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
    public async Task MissingReadDoesNotCreateTheDataDirectory()
    {
        var store = Store();

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Missing,
            result.State);
        Assert.IsNull(result.Checkpoint);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task RoundTripUsesStrictStringEnumsAndLeavesNoTemporaryFile()
    {
        var store = Store();
        var checkpoint = Checkpoint(
            BridgeHostCutoverStage.DotNetStartRequested,
            dotNetProcessId: 82001);

        await store.WriteAsync(checkpoint);
        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Present,
            result.State);
        Assert.AreEqual(checkpoint, result.Checkpoint);
        var json = await File.ReadAllTextAsync(store.CheckpointPath);
        StringAssert.Contains(json, "\"stage\":\"DotNetStartRequested\"");
        StringAssert.Contains(json, "\"failureReason\":\"None\"");
        Assert.AreEqual(
            0,
            Directory.EnumerateFiles(directory!, "*.tmp").Count());
    }

    [TestMethod]
    public async Task SuccessfulReplacementLeavesOnlyTheCurrentCheckpoint()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        await store.WriteAsync(
            Checkpoint(
                BridgeHostCutoverStage.Completed,
                operationId: "cutover-2",
                dotNetProcessId: 82002));

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Present,
            result.State);
        Assert.AreEqual("cutover-2", result.Checkpoint?.OperationId);
        Assert.AreEqual(
            1,
            Directory.EnumerateFiles(
                directory!,
                BridgeHostCutoverCheckpointStore.CheckpointFileName).Count());
        Assert.AreEqual(
            0,
            Directory.EnumerateFiles(directory!, "*.tmp").Count());
    }

    [TestMethod]
    public async Task UnknownRootFieldsAreInvalidAndDoNotRewriteTheFile()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        var original = await File.ReadAllTextAsync(store.CheckpointPath);
        using var document = JsonDocument.Parse(original);
        var fields = document.RootElement.EnumerateObject()
            .Select(property => $"\"{property.Name}\":{property.Value.GetRawText()}")
            .Append("\"futureField\":true");
        await File.WriteAllTextAsync(
            store.CheckpointPath,
            "{" + string.Join(",", fields) + "}\n");
        var beforeRead = await File.ReadAllTextAsync(store.CheckpointPath);

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Invalid,
            result.State);
        Assert.IsNull(result.Checkpoint);
        Assert.AreEqual(beforeRead, await File.ReadAllTextAsync(store.CheckpointPath));
    }

    [TestMethod]
    public async Task UnknownNestedFieldsAreInvalid()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        var json = await File.ReadAllTextAsync(store.CheckpointPath);
        using var document = JsonDocument.Parse(json);
        var rootFields = document.RootElement.EnumerateObject()
            .Select(property => property.Name == "expectedNode"
                ? "\"expectedNode\":{" + string.Join(
                    ",",
                    property.Value.EnumerateObject()
                        .Select(item => $"\"{item.Name}\":{item.Value.GetRawText()}")
                        .Append("\"futureNodeField\":1")) + "}"
                : $"\"{property.Name}\":{property.Value.GetRawText()}");
        await File.WriteAllTextAsync(
            store.CheckpointPath,
            "{" + string.Join(",", rootFields) + "}\n");

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Invalid,
            result.State);
    }

    [DataTestMethod]
    [DataRow("dotnetstartrequested", "None")]
    [DataRow("DotNetStartRequested", "none")]
    [DataRow("1", "None")]
    [DataRow("DotNetStartRequested", "1")]
    public async Task EnumValuesMustBeExactStrings(
        string stage,
        string failureReason)
    {
        var store = Store();
        await store.WriteAsync(
            Checkpoint(
                BridgeHostCutoverStage.DotNetStartRequested,
                dotNetProcessId: 82010));
        var json = await File.ReadAllTextAsync(store.CheckpointPath);
        json = json.Replace(
            "\"stage\":\"DotNetStartRequested\"",
            $"\"stage\":\"{stage}\"",
            StringComparison.Ordinal);
        json = json.Replace(
            "\"failureReason\":\"None\"",
            $"\"failureReason\":\"{failureReason}\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(store.CheckpointPath, json);

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Invalid,
            result.State);
    }

    [DataTestMethod]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{\"schemaVersion\":\"bad\"}")]
    public async Task MalformedOrWrongShapeJsonIsInvalid(string json)
    {
        var store = Store();
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(store.CheckpointPath, json);

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Invalid,
            result.State);
    }

    [TestMethod]
    public async Task ADirectoryAtTheCheckpointPathIsInvalid()
    {
        Directory.CreateDirectory(Path.Combine(
            directory!,
            BridgeHostCutoverCheckpointStore.CheckpointFileName));
        var result = await Store().ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Invalid,
            result.State);
    }

    [TestMethod]
    public async Task AFileAtTheDataDirectoryPathIsNotTreatedAsMissing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(directory!)!);
        await File.WriteAllTextAsync(directory!, "not-a-directory");

        var result = await Store().ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Invalid,
            result.State);
    }

    [TestMethod]
    public async Task LockedCheckpointIsUnavailableInsteadOfMissing()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        await using var locked = new FileStream(
            store.CheckpointPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = await store.ReadAsync();

        Assert.AreEqual(
            BridgeHostCutoverCheckpointReadState.Unavailable,
            result.State);
        Assert.IsNull(result.Checkpoint);
    }

    [TestMethod]
    public async Task InvalidCheckpointCannotReplaceAnExistingFile()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        var before = await File.ReadAllTextAsync(store.CheckpointPath);
        var invalid = Checkpoint(BridgeHostCutoverStage.Completed) with
        {
            DotNetProcessId = 0,
        };

        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await store.WriteAsync(invalid));

        Assert.AreEqual(before, await File.ReadAllTextAsync(store.CheckpointPath));
        Assert.AreEqual(0, Directory.EnumerateFiles(directory!, "*.tmp").Count());
    }

    [TestMethod]
    public async Task CancellationBeforeWriteLeavesAnExistingCheckpointUntouched()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        var before = await File.ReadAllTextAsync(store.CheckpointPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await store.WriteAsync(
                Checkpoint(BridgeHostCutoverStage.Completed, dotNetProcessId: 82003),
                cancellation.Token));

        Assert.AreEqual(before, await File.ReadAllTextAsync(store.CheckpointPath));
        Assert.AreEqual(0, Directory.EnumerateFiles(directory!, "*.tmp").Count());
    }

    [TestMethod]
    public async Task FailedReplacementCleansItsTemporaryFile()
    {
        var store = Store();
        Directory.CreateDirectory(store.CheckpointPath);

        Exception? error = null;
        try
        {
            await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        }
        catch (Exception exception)
        {
            error = exception;
        }

        Assert.IsNotNull(error);
        Assert.IsTrue(error is IOException or UnauthorizedAccessException);
        Assert.AreEqual(0, Directory.EnumerateFiles(directory!, "*.tmp").Count());
    }

    [TestMethod]
    public void ExportCapturesTransactionProcessIdsAndStage()
    {
        var transaction = BridgeHostCutoverTransaction.Create(
            Node(82004),
            "production-dotnet");
        transaction.Apply(new NodeStopRequestedEvent(Node(82004)));
        transaction.Apply(new NodeOfflineVerifiedEvent(82004));
        transaction.Apply(
            new StoreHandoffVerifiedEvent(
                new BridgeStoreHandoffEvidence(
                    true,
                    true,
                    BridgeCutoverLeaseState.Missing)));
        transaction.Apply(new DotNetStartRequestedEvent(82005));

        var checkpoint = transaction.ExportCheckpoint(
            "export-test",
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"));

        Assert.AreEqual(
            BridgeHostCutoverStage.DotNetStartRequested,
            checkpoint.Stage);
        Assert.AreEqual(82005, checkpoint.DotNetProcessId);
        Assert.AreEqual(0, checkpoint.NodeRollbackProcessId);
        Assert.AreEqual(Node(82004), checkpoint.ExpectedNode);
        Assert.AreEqual(checkpoint, checkpoint.Validate());
    }

    [TestMethod]
    public async Task EarlyRollbackCheckpointsDoNotRequireADotNetProcess()
    {
        var transaction = BridgeHostCutoverTransaction.Create(
            Node(82020),
            "production-dotnet");
        transaction.Apply(new NodeStopRequestedEvent(Node(82020)));
        transaction.Apply(new NodeOfflineVerifiedEvent(82020));
        transaction.Apply(
            new StoreHandoffVerifiedEvent(
                new BridgeStoreHandoffEvidence(
                    false,
                    true,
                    BridgeCutoverLeaseState.Missing)));

        var rollbackRequired = transaction.ExportCheckpoint(
            "early-rollback-required",
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"));
        Assert.AreEqual(BridgeHostCutoverStage.RollbackRequired, rollbackRequired.Stage);
        Assert.AreEqual(0, rollbackRequired.DotNetProcessId);

        transaction.Apply(new NodeRollbackStartRequestedEvent(82021));
        var rollbackStarted = transaction.ExportCheckpoint(
            "early-rollback-started",
            DateTimeOffset.Parse("2026-08-07T12:00:01.000Z"));
        Assert.AreEqual(
            BridgeHostCutoverStage.NodeRollbackStartRequested,
            rollbackStarted.Stage);
        Assert.AreEqual(0, rollbackStarted.DotNetProcessId);
        Assert.AreEqual(82021, rollbackStarted.NodeRollbackProcessId);

        transaction.Apply(new NodeRollbackActiveVerifiedEvent(Node(82021)));
        var rolledBack = transaction.ExportCheckpoint(
            "early-rollback-completed",
            DateTimeOffset.Parse("2026-08-07T12:00:02.000Z"));
        Assert.AreEqual(BridgeHostCutoverStage.RolledBack, rolledBack.Stage);
        Assert.AreEqual(0, rolledBack.DotNetProcessId);
        Assert.AreEqual(82021, rolledBack.NodeRollbackProcessId);

        var store = Store();
        await store.WriteAsync(rolledBack);
        var read = await store.ReadAsync();
        Assert.AreEqual(rolledBack, read.Checkpoint);
    }

    [TestMethod]
    public void FailedSafeWithoutRollbackCannotClaimStartedProcesses()
    {
        var invalid = Checkpoint(BridgeHostCutoverStage.FailedSafe) with
        {
            RequiresRollback = false,
            DotNetProcessId = 82030,
        };

        Assert.ThrowsException<InvalidDataException>(() => invalid.Validate());
    }

    [TestMethod]
    public void ExportRejectsUnsafeOperationIds()
    {
        var transaction = BridgeHostCutoverTransaction.Create(
            Node(82006),
            "production-dotnet");

        Assert.ThrowsException<InvalidDataException>(() =>
            transaction.ExportCheckpoint(
                "../outside",
                DateTimeOffset.Parse("2026-08-07T12:00:00.000Z")));
    }

    [TestMethod]
    public async Task CheckpointJsonDoesNotContainTokensPathsOrLeaseIds()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Planned));
        var json = await File.ReadAllTextAsync(store.CheckpointPath);

        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(
            "control|token|leaseId|cwd|path|secret|password",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        StringAssert.Contains(json, "expectedNode");
        StringAssert.Contains(json, "operationId");
    }

    private BridgeHostCutoverCheckpointStore Store() =>
        new(directory!);

    private static BridgeHostCutoverCheckpoint Checkpoint(
        BridgeHostCutoverStage stage,
        string operationId = "cutover-1",
        int dotNetProcessId = 0) =>
        new(
            BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
            operationId,
            DateTimeOffset.Parse("2026-08-07T12:00:00.000Z"),
            stage,
            RequiresRollback(stage),
            FailureReason(stage),
            Node(82000),
            "production-dotnet",
            dotNetProcessId,
            NodeRollbackProcessId(stage));

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

    private static int NodeRollbackProcessId(BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.NodeRollbackStartRequested or
            BridgeHostCutoverStage.RolledBack
            ? 82009
            : 0;

    private static BridgeCutoverHostIdentity Node(int processId) =>
        new(
            processId,
            "node",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            true,
            "production");
}
