using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostProductionTargetSelectorTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-production-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
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
    public async Task MissingCheckpointSelectsNodeProduction()
    {
        var target = await Selector().SelectAsync();

        Assert.AreEqual(BridgeHostMode.NodeProduction, target.Mode);
        Assert.AreEqual(8765, target.Port);
        Assert.AreEqual("production", target.InstanceName);
    }

    [TestMethod]
    public async Task TerminalCheckpointPersistentlySelectsCommittedOwner()
    {
        var store = Store();
        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.Completed));
        var committed = await Selector().SelectAsync();
        Assert.AreEqual(BridgeHostMode.DotNetProduction, committed.Mode);
        Assert.AreEqual(
            BridgeHostTarget.DotNetProductionInstanceName,
            committed.InstanceName);

        await store.WriteAsync(Checkpoint(BridgeHostCutoverStage.RolledBack));
        var rolledBack = await Selector().SelectAsync();
        Assert.AreEqual(BridgeHostMode.NodeProduction, rolledBack.Mode);
    }

    [TestMethod]
    public async Task NonTerminalCheckpointRequiresRecoveryInsteadOfGuessing()
    {
        await Store().WriteAsync(Checkpoint(BridgeHostCutoverStage.DotNetActiveVerified));

        var error = await Assert.ThrowsExceptionAsync<
            BridgeHostProductionTargetSelectionException>(async () =>
            await Selector().SelectAsync());

        Assert.AreEqual(
            BridgeHostProductionTargetSelectionFailure.RecoveryRequired,
            error.Failure);
        StringAssert.DoesNotMatch(
            error.Message,
            new System.Text.RegularExpressions.Regex("8200[0-9]"));
    }

    [TestMethod]
    public async Task InvalidOrUnsupportedCheckpointFailsClosed()
    {
        await File.WriteAllTextAsync(
            Store().CheckpointPath,
            "{\"schemaVersion\":999}");
        var invalid = await Assert.ThrowsExceptionAsync<
            BridgeHostProductionTargetSelectionException>(async () =>
            await Selector().SelectAsync());
        Assert.AreEqual(
            BridgeHostProductionTargetSelectionFailure.InvalidCheckpoint,
            invalid.Failure);

        await Store().WriteAsync(
            Checkpoint(BridgeHostCutoverStage.Completed) with
            {
                ExpectedDotNetInstanceName = "another-production",
            });
        var unsupported = await Assert.ThrowsExceptionAsync<
            BridgeHostProductionTargetSelectionException>(async () =>
            await Selector().SelectAsync());
        Assert.AreEqual(
            BridgeHostProductionTargetSelectionFailure.UnsupportedIdentity,
            unsupported.Failure);
    }

    [TestMethod]
    public async Task OrphanedCheckpointFileBlocksDefaultNodeSelection()
    {
        await File.WriteAllTextAsync(
            Path.Combine(
                directory!,
                $"{BridgeHostCutoverCheckpointStore.CheckpointFileName}.1234." +
                $"{Guid.NewGuid():N}.tmp"),
            "{}");

        var error = await Assert.ThrowsExceptionAsync<
            BridgeHostProductionTargetSelectionException>(async () =>
            await Selector().SelectAsync());

        Assert.AreEqual(
            BridgeHostProductionTargetSelectionFailure.RecoveryRequired,
            error.Failure);
    }

    [TestMethod]
    public async Task ChangingObservationAndRefreshFailurePreserveCurrentTarget()
    {
        var reads = new Queue<BridgeHostCutoverCheckpointReadResult>(new[]
        {
            new BridgeHostCutoverCheckpointReadResult(
                BridgeHostCutoverCheckpointReadState.Missing),
            new BridgeHostCutoverCheckpointReadResult(
                BridgeHostCutoverCheckpointReadState.Present,
                Checkpoint(BridgeHostCutoverStage.Completed),
                "changed-version"),
        });
        var selector = new BridgeHostProductionTargetSelector(
            directory!,
            8765,
            _ => ValueTask.FromResult(reads.Dequeue()),
            _ => BridgeHostCutoverCheckpointRecoveryState.Clean);
        var changed = await Assert.ThrowsExceptionAsync<
            BridgeHostProductionTargetSelectionException>(async () =>
            await selector.SelectAsync());
        Assert.AreEqual(
            BridgeHostProductionTargetSelectionFailure.ObservationChanged,
            changed.Failure);

        var attempts = 0;
        var state = new BridgeHostTargetState(
            BridgeHostTarget.NodeProduction(8765),
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromResult(BridgeHostTarget.DotNetProduction(8765))
                    : ValueTask.FromException<BridgeHostTarget>(
                        new InvalidOperationException("selection failed"));
            });
        Assert.AreEqual(
            BridgeHostMode.DotNetProduction,
            (await state.RefreshAsync()).Mode);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await state.RefreshAsync());
        Assert.AreEqual(BridgeHostMode.DotNetProduction, state.Current.Mode);

        var unbound = new BridgeHostTargetState(
            BridgeHostTarget.NodeProduction(8765),
            _ => ValueTask.FromResult(
                BridgeHostTarget.DotNetProduction(8765, "another-production")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await unbound.RefreshAsync());
        Assert.AreEqual(BridgeHostMode.NodeProduction, unbound.Current.Mode);
    }

    [TestMethod]
    public async Task ShadowTargetNeverConsultsProductionCheckpoint()
    {
        var calls = 0;
        var state = new BridgeHostTargetState(
            BridgeHostTarget.DotNetShadow(),
            _ =>
            {
                calls++;
                throw new InvalidOperationException();
            });

        var target = await state.RefreshAsync();

        Assert.AreEqual(BridgeHostMode.DotNetShadow, target.Mode);
        Assert.AreEqual(0, calls);
    }

    private BridgeHostProductionTargetSelector Selector() =>
        new(directory!, 8765);

    private BridgeHostCutoverCheckpointStore Store() =>
        new(directory!);

    private static BridgeHostCutoverCheckpoint Checkpoint(
        BridgeHostCutoverStage stage) =>
        new(
            BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
            "production-target-test",
            DateTimeOffset.Parse("2026-08-08T12:00:00.000Z"),
            stage,
            RequiresRollback: false,
            stage is BridgeHostCutoverStage.RolledBack
                ? BridgeCutoverFailureReason.OwnershipUncertain
                : BridgeCutoverFailureReason.None,
            new BridgeCutoverHostIdentity(
                82001,
                "node",
                BridgeHostCutoverTransaction.CurrentManagementApiVersion,
                "active",
                ActiveOwner: true,
                InstanceName: "production"),
            BridgeHostTarget.DotNetProductionInstanceName,
            DotNetProcessId: stage is BridgeHostCutoverStage.RolledBack ? 0 : 82002,
            NodeRollbackProcessId: stage is BridgeHostCutoverStage.RolledBack ? 82003 : 0);
}
