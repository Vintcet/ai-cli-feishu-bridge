using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostStartupRecoveryTests
{
    private const string FileVersion = "stable-version";

    [TestMethod]
    public async Task MissingCheckpointRefreshesTargetWithoutExecutingRecovery()
    {
        var executions = 0;
        var refreshes = 0;
        var recovery = Recovery(
            Missing(),
            execute: _ =>
            {
                executions++;
                return ValueTask.FromResult(
                    new BridgeHostRecoveryExecutionResult(
                        BridgeHostRecoveryExecutionState.Recovered));
            },
            refresh: _ =>
            {
                refreshes++;
                return ValueTask.CompletedTask;
            });

        var result = await recovery.RunAsync();

        Assert.AreEqual(BridgeHostStartupRecoveryState.NotRequired, result.State);
        Assert.IsTrue(result.CanContinue);
        Assert.AreEqual(0, executions);
        Assert.AreEqual(1, refreshes);
    }

    [TestMethod]
    public async Task PresentCheckpointExecutesRecoveryAndRefreshesOnlyOnSuccess()
    {
        var refreshes = 0;
        var recovery = Recovery(
            Present(),
            execute: _ => ValueTask.FromResult(
                new BridgeHostRecoveryExecutionResult(
                    BridgeHostRecoveryExecutionState.Recovered)),
            refresh: _ =>
            {
                refreshes++;
                return ValueTask.CompletedTask;
            });

        var result = await recovery.RunAsync();

        Assert.AreEqual(BridgeHostStartupRecoveryState.Recovered, result.State);
        Assert.AreEqual(1, refreshes);

        refreshes = 0;
        recovery = Recovery(
            Present(),
            execute: _ => ValueTask.FromResult(
                new BridgeHostRecoveryExecutionResult(
                    BridgeHostRecoveryExecutionState.ManualIntervention,
                    new BridgeHostRecoveryPlan(
                        BridgeHostRecoveryDisposition.ManualIntervention,
                        BridgeHostRecoveryReason.LeaseIdentityMismatch))),
            refresh: _ =>
            {
                refreshes++;
                return ValueTask.CompletedTask;
            });

        result = await recovery.RunAsync();

        Assert.AreEqual(
            BridgeHostStartupRecoveryState.ManualIntervention,
            result.State);
        Assert.AreEqual(BridgeHostRecoveryReason.LeaseIdentityMismatch, result.Reason);
        Assert.AreEqual(0, refreshes);
        Assert.IsFalse(result.CanContinue);
    }

    [TestMethod]
    public async Task UncertainCheckpointStatesNeverExecuteOwnershipOperations()
    {
        foreach (var fixture in new[]
        {
            (Read: Invalid(), Recovery: BridgeHostCutoverCheckpointRecoveryState.Clean,
                Expected: BridgeHostStartupRecoveryState.ManualIntervention),
            (Read: Unavailable(), Recovery: BridgeHostCutoverCheckpointRecoveryState.Clean,
                Expected: BridgeHostStartupRecoveryState.Unavailable),
            (Read: Missing(), Recovery: BridgeHostCutoverCheckpointRecoveryState.RecoveryRequired,
                Expected: BridgeHostStartupRecoveryState.CheckpointRecoveryRequired),
            (Read: Missing(), Recovery: BridgeHostCutoverCheckpointRecoveryState.Unavailable,
                Expected: BridgeHostStartupRecoveryState.Unavailable),
        })
        {
            var executions = 0;
            var refreshes = 0;
            var recovery = Recovery(
                fixture.Read,
                execute: _ =>
                {
                    executions++;
                    throw new InvalidOperationException();
                },
                refresh: _ =>
                {
                    refreshes++;
                    return ValueTask.CompletedTask;
                },
                recoveryState: fixture.Recovery);

            var result = await recovery.RunAsync();

            Assert.AreEqual(fixture.Expected, result.State);
            Assert.AreEqual(0, executions);
            Assert.AreEqual(0, refreshes);
        }
    }

    [TestMethod]
    public async Task ChangingCheckpointBlocksRecoveryBeforeSideEffects()
    {
        var reads = new Queue<BridgeHostCutoverCheckpointReadResult>(new[]
        {
            Missing(),
            Present(),
        });
        var executions = 0;
        var recovery = new BridgeHostStartupRecovery(
            Path.GetTempPath(),
            _ => ValueTask.FromResult(reads.Dequeue()),
            _ => BridgeHostCutoverCheckpointRecoveryState.Clean,
            _ =>
            {
                executions++;
                throw new InvalidOperationException();
            },
            _ => ValueTask.CompletedTask);

        var result = await recovery.RunAsync();

        Assert.AreEqual(
            BridgeHostStartupRecoveryState.ManualIntervention,
            result.State);
        Assert.AreEqual(BridgeHostRecoveryReason.CheckpointChanged, result.Reason);
        Assert.AreEqual(0, executions);
    }

    [TestMethod]
    public async Task ExecutionAndRefreshFailuresRemainFailClosed()
    {
        var executionFailure = Recovery(
            Present(),
            execute: _ => throw new InvalidOperationException("sensitive failure"));
        var executionResult = await executionFailure.RunAsync();
        Assert.AreEqual(
            BridgeHostStartupRecoveryState.Unavailable,
            executionResult.State);
        Assert.IsFalse(
            executionResult.UserMessage.Contains(
                "sensitive failure",
                StringComparison.Ordinal));

        var refreshFailure = Recovery(
            Present(),
            execute: _ => ValueTask.FromResult(
                new BridgeHostRecoveryExecutionResult(
                    BridgeHostRecoveryExecutionState.NoActionRequired)),
            refresh: _ => throw new InvalidOperationException("refresh failure"));
        var refreshResult = await refreshFailure.RunAsync();
        Assert.AreEqual(
            BridgeHostStartupRecoveryState.ManualIntervention,
            refreshResult.State);
        Assert.AreEqual(BridgeHostRecoveryReason.CheckpointChanged, refreshResult.Reason);
    }

    [TestMethod]
    public void PublicMessagesDoNotExposeRecoveryEvidence()
    {
        foreach (var state in Enum.GetValues<BridgeHostStartupRecoveryState>())
        {
            var message = new BridgeHostStartupRecoveryResult(
                state,
                BridgeHostRecoveryReason.LeaseIdentityMismatch).UserMessage;
            StringAssert.DoesNotMatch(
                message,
                new System.Text.RegularExpressions.Regex(
                    "pid|leaseId|operationId|control.?token|[A-Z]:\\\\",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
    }

    private static BridgeHostStartupRecovery Recovery(
        BridgeHostCutoverCheckpointReadResult read,
        Func<CancellationToken, ValueTask<BridgeHostRecoveryExecutionResult>>?
            execute = null,
        Func<CancellationToken, ValueTask>? refresh = null,
        BridgeHostCutoverCheckpointRecoveryState recoveryState =
            BridgeHostCutoverCheckpointRecoveryState.Clean) =>
        new(
            Path.GetTempPath(),
            _ => ValueTask.FromResult(read),
            _ => recoveryState,
            execute ?? (_ => ValueTask.FromResult(
                new BridgeHostRecoveryExecutionResult(
                    BridgeHostRecoveryExecutionState.NoActionRequired))),
            refresh ?? (_ => ValueTask.CompletedTask));

    private static BridgeHostCutoverCheckpointReadResult Missing() =>
        new(BridgeHostCutoverCheckpointReadState.Missing);

    private static BridgeHostCutoverCheckpointReadResult Invalid() =>
        new(BridgeHostCutoverCheckpointReadState.Invalid);

    private static BridgeHostCutoverCheckpointReadResult Unavailable() =>
        new(BridgeHostCutoverCheckpointReadState.Unavailable);

    private static BridgeHostCutoverCheckpointReadResult Present() =>
        new(
            BridgeHostCutoverCheckpointReadState.Present,
            new BridgeHostCutoverCheckpoint(
                BridgeHostCutoverCheckpoint.CurrentSchemaVersion,
                "startup-recovery-test",
                DateTimeOffset.Parse("2026-08-08T13:00:00.000Z"),
                BridgeHostCutoverStage.Completed,
                RequiresRollback: false,
                BridgeCutoverFailureReason.None,
                new BridgeCutoverHostIdentity(
                    82001,
                    "node",
                    BridgeHostCutoverTransaction.CurrentManagementApiVersion,
                    "active",
                    ActiveOwner: true,
                    InstanceName: "production"),
                BridgeHostTarget.DotNetProductionInstanceName,
                DotNetProcessId: 82002,
                NodeRollbackProcessId: 0),
            FileVersion);
}
