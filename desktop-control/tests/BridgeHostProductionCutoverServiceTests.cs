using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl.Tests;

[TestClass]
public sealed class BridgeHostProductionCutoverServiceTests
{
    [TestMethod]
    public async Task CompletedCutoverUsesExactAuthenticatedNodeAndRefreshesDotNet()
    {
        var target = BridgeHostTarget.NodeProduction(9123);
        var current = target;
        BridgeCutoverHostIdentity? observedIdentity = null;
        var refreshes = 0;
        var service = Service(
            target,
            () => current,
            status: NodeStatus(),
            execute: identity =>
            {
                observedIdentity = identity;
                return new(BridgeHostPersistentCutoverState.Completed);
            },
            refresh: () =>
            {
                refreshes++;
                current = BridgeHostTarget.DotNetProduction(9123);
            });

        var result = await service.RunAsync();

        Assert.AreEqual(BridgeHostProductionCutoverState.Completed, result.State);
        Assert.IsNotNull(observedIdentity);
        Assert.AreEqual(81234, observedIdentity.ProcessId);
        Assert.AreEqual("node", observedIdentity.HostKind);
        Assert.AreEqual(1, observedIdentity.ManagementApiVersion);
        Assert.AreEqual("active", observedIdentity.OwnershipMode);
        Assert.IsTrue(observedIdentity.ActiveOwner);
        Assert.AreEqual("production", observedIdentity.InstanceName);
        Assert.AreEqual(1, refreshes);
        Assert.IsFalse(result.RequiresOwnershipLock);
    }

    [TestMethod]
    public async Task RolledBackCutoverRefreshesNodeTarget()
    {
        var target = BridgeHostTarget.NodeProduction(9123);
        var current = target;
        var refreshes = 0;
        var service = Service(
            target,
            () => current,
            status: NodeStatus(),
            execute: _ => new(BridgeHostPersistentCutoverState.RolledBack),
            refresh: () =>
            {
                refreshes++;
                current = BridgeHostTarget.NodeProduction(9123);
            });

        var result = await service.RunAsync();

        Assert.AreEqual(BridgeHostProductionCutoverState.RolledBack, result.State);
        Assert.AreEqual(1, refreshes);
        Assert.IsFalse(result.RequiresOwnershipLock);
    }

    [DataTestMethod]
    [DataRow("not-ok")]
    [DataRow("pid")]
    [DataRow("host")]
    [DataRow("api")]
    [DataRow("ownership")]
    [DataRow("active-owner")]
    [DataRow("instance")]
    public async Task CutoverRejectsEveryInexactNodeIdentity(string mismatch)
    {
        var target = BridgeHostTarget.NodeProduction(9123);
        var status = NodeStatus();
        switch (mismatch)
        {
            case "not-ok":
                status.Ok = false;
                break;
            case "pid":
                status.ProcessId = 0;
                break;
            case "host":
                status.HostKind = "dotnet";
                break;
            case "api":
                status.ManagementApiVersion = 2;
                break;
            case "ownership":
                status.OwnershipMode = "passive";
                break;
            case "active-owner":
                status.ActiveOwner = false;
                break;
            case "instance":
                status.InstanceName = "another-production";
                break;
        }
        var executions = 0;
        var service = Service(
            target,
            () => target,
            status,
            _ =>
            {
                executions++;
                return new(BridgeHostPersistentCutoverState.Completed);
            });

        var result = await service.RunAsync();

        Assert.AreEqual(BridgeHostProductionCutoverState.Unavailable, result.State);
        Assert.AreEqual(0, executions);
        Assert.IsTrue(result.RequiresOwnershipLock);
    }

    [DataTestMethod]
    [DataRow("FailedSafe", "FailedSafe")]
    [DataRow("Busy", "Busy")]
    [DataRow("CheckpointRecoveryRequired", "CheckpointRecoveryRequired")]
    [DataRow("CheckpointConflict", "CheckpointConflict")]
    [DataRow("Unavailable", "Unavailable")]
    [DataRow("Cancelled", "Cancelled")]
    public async Task NonTerminalResultsUseFixedMappingAndDoNotRefresh(
        string persistentStateName,
        string expectedStateName)
    {
        var target = BridgeHostTarget.NodeProduction(9123);
        var refreshes = 0;
        var persistentState = Enum.Parse<BridgeHostPersistentCutoverState>(
            persistentStateName);
        var service = Service(
            target,
            () => target,
            status: NodeStatus(),
            execute: _ => new(persistentState),
            refresh: () => refreshes++);

        var result = await service.RunAsync();

        Assert.AreEqual(
            Enum.Parse<BridgeHostProductionCutoverState>(expectedStateName),
            result.State);
        Assert.AreEqual(0, refreshes);
        Assert.IsTrue(result.RequiresOwnershipLock);
        Assert.IsFalse(result.UserMessage.Contains("81234", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StartupRecoveryBlockPreventsStatusProbeAndCutover()
    {
        var target = BridgeHostTarget.NodeProduction(9123);
        var statusReads = 0;
        var executions = 0;
        var service = new BridgeHostProductionCutoverService(
            target,
            _ => ValueTask.FromResult(new BridgeHostStartupRecoveryResult(
                BridgeHostStartupRecoveryState.CheckpointRecoveryRequired)),
            () => target,
            _ =>
            {
                statusReads++;
                return ValueTask.FromResult<BridgeStatus?>(NodeStatus());
            },
            (_, _) =>
            {
                executions++;
                return ValueTask.FromResult(new BridgeHostPersistentCutoverResult(
                    BridgeHostPersistentCutoverState.Completed));
            },
            _ => ValueTask.CompletedTask);

        var result = await service.RunAsync();

        Assert.AreEqual(
            BridgeHostProductionCutoverState.CheckpointRecoveryRequired,
            result.State);
        Assert.AreEqual(0, statusReads);
        Assert.AreEqual(0, executions);
    }

    [TestMethod]
    public async Task AlreadyDotNetProductionDoesNotProbeOrExecuteNodeCutover()
    {
        var nodeTarget = BridgeHostTarget.NodeProduction(9123);
        var current = BridgeHostTarget.DotNetProduction(9123);
        var statusReads = 0;
        var executions = 0;
        var service = new BridgeHostProductionCutoverService(
            nodeTarget,
            _ => ValueTask.FromResult(new BridgeHostStartupRecoveryResult(
                BridgeHostStartupRecoveryState.NoActionRequired)),
            () => current,
            _ =>
            {
                statusReads++;
                return ValueTask.FromResult<BridgeStatus?>(NodeStatus());
            },
            (_, _) =>
            {
                executions++;
                return ValueTask.FromResult(new BridgeHostPersistentCutoverResult(
                    BridgeHostPersistentCutoverState.Completed));
            },
            _ => ValueTask.CompletedTask);

        var result = await service.RunAsync();

        Assert.AreEqual(
            BridgeHostProductionCutoverState.NotNodeProduction,
            result.State);
        Assert.AreEqual(0, statusReads);
        Assert.AreEqual(0, executions);
        Assert.IsFalse(result.RequiresOwnershipLock);
    }

    [TestMethod]
    public async Task TerminalResultLocksWhenTargetCannotBeRefreshedExactly()
    {
        var target = BridgeHostTarget.NodeProduction(9123);
        var service = Service(
            target,
            () => target,
            status: NodeStatus(),
            execute: _ => new(BridgeHostPersistentCutoverState.Completed));

        var result = await service.RunAsync();

        Assert.AreEqual(BridgeHostProductionCutoverState.Unavailable, result.State);
        Assert.IsTrue(result.RequiresOwnershipLock);
    }

    private static BridgeHostProductionCutoverService Service(
        BridgeHostTarget expectedNodeTarget,
        Func<BridgeHostTarget> getCurrentTarget,
        BridgeStatus status,
        Func<BridgeCutoverHostIdentity, BridgeHostPersistentCutoverResult> execute,
        Action? refresh = null) =>
        new(
            expectedNodeTarget,
            _ => ValueTask.FromResult(new BridgeHostStartupRecoveryResult(
                BridgeHostStartupRecoveryState.NotRequired)),
            getCurrentTarget,
            _ => ValueTask.FromResult<BridgeStatus?>(status),
            (identity, _) => ValueTask.FromResult(execute(identity)),
            _ =>
            {
                refresh?.Invoke();
                return ValueTask.CompletedTask;
            });

    private static BridgeStatus NodeStatus() => new()
    {
        Ok = true,
        ProcessId = 81234,
        HostKind = "node",
        ManagementApiVersion = BridgeHostTarget.CurrentManagementApiVersion,
        InstanceName = "production",
        OwnershipMode = "active",
        ActiveOwner = true,
    };
}
