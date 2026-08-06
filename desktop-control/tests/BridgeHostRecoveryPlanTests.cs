using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class BridgeHostRecoveryPlanTests
{
    private const string NodeInstanceName = "production";
    private const string DotNetInstanceName = "production-dotnet";

    [TestMethod]
    public void AnUncommittedCutoverKeepsAVerifiedNodeOwner()
    {
        var identity = Node(processId: 81001);

        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.FailedSafe, requiresRollback: true),
            Authenticated(identity, LiveLease(identity, "node-live")));

        Assert.AreEqual(
            BridgeHostRecoveryDisposition.NodeAlreadyActive,
            plan.Disposition);
        Assert.AreEqual(BridgeHostRecoveryReason.None, plan.Reason);
        Assert.IsFalse(plan.HasAutomaticSteps);
        Assert.IsFalse(plan.RequiresManualIntervention);
        Assert.AreEqual(0, plan.Steps.Count);
    }

    [TestMethod]
    public void ACommittedCutoverKeepsAVerifiedDotNetOwner()
    {
        var identity = DotNet(processId: 81002);

        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.Completed),
            Authenticated(identity, LiveLease(identity, "dotnet-live")));

        Assert.AreEqual(
            BridgeHostRecoveryDisposition.DotNetAlreadyActive,
            plan.Disposition);
        Assert.AreEqual(BridgeHostRecoveryReason.None, plan.Reason);
        Assert.AreEqual(0, plan.Steps.Count);
    }

    [TestMethod]
    public void OfflineMissingOwnerRestartsNodeBeforeTheCommitPoint()
    {
        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.DotNetActiveVerified),
            Offline(new(ActiveOwnerLeaseState.Missing)));

        Assert.AreEqual(BridgeHostRecoveryDisposition.RestartNode, plan.Disposition);
        AssertSteps(
            plan,
            BridgeHostRecoveryStep.InspectStoreHandoff,
            BridgeHostRecoveryStep.StartNode,
            BridgeHostRecoveryStep.VerifyNodeActive);
    }

    [TestMethod]
    public void OfflineMissingOwnerRestartsDotNetAfterTheCommitPoint()
    {
        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.Completed),
            Offline(new(ActiveOwnerLeaseState.Missing)));

        Assert.AreEqual(BridgeHostRecoveryDisposition.RestartDotNet, plan.Disposition);
        AssertSteps(
            plan,
            BridgeHostRecoveryStep.InspectStoreHandoff,
            BridgeHostRecoveryStep.StartDotNet,
            BridgeHostRecoveryStep.VerifyDotNetActive);
    }

    [DataTestMethod]
    [DataRow("DotNetStartRequested", false)]
    [DataRow("DotNetActiveVerified", false)]
    [DataRow("RollbackRequired", true)]
    [DataRow("FailedSafe", true)]
    public void DotNetBeforeTheCommitPointAlwaysRollsBackToNode(
        string stageName,
        bool requiresRollback)
    {
        var stage = Enum.Parse<BridgeHostCutoverStage>(stageName);
        var identity = DotNet(processId: 81003);

        var plan = Planner().Plan(
            Snapshot(stage, requiresRollback),
            Authenticated(identity, LiveLease(identity, "dotnet-uncommitted")));

        Assert.AreEqual(
            BridgeHostRecoveryDisposition.RollBackDotNetToNode,
            plan.Disposition);
        AssertSteps(
            plan,
            BridgeHostRecoveryStep.RequestDotNetStop,
            BridgeHostRecoveryStep.VerifyDotNetOffline,
            BridgeHostRecoveryStep.InspectStoreHandoff,
            BridgeHostRecoveryStep.StartNode,
            BridgeHostRecoveryStep.VerifyNodeActive);
    }

    [TestMethod]
    public void NodeAfterADotNetCommitRequiresManualIntervention()
    {
        var identity = Node(processId: 81004);

        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.Completed),
            Authenticated(identity, LiveLease(identity, "unexpected-node")));

        AssertManual(plan, BridgeHostRecoveryReason.UnexpectedCommittedOwner);
    }

    [TestMethod]
    public void AnUncertainEndpointNeverStartsOrStopsAnything()
    {
        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.FailedSafe, requiresRollback: true),
            new(
                BridgeHostRecoveryEndpointObservation.Uncertain(),
                new ActiveOwnerLeaseSnapshot(ActiveOwnerLeaseState.Missing)));

        AssertManual(plan, BridgeHostRecoveryReason.EndpointUncertain);
    }

    [DataTestMethod]
    [DataRow("Stale", "ActiveOwnerLeaseStale")]
    [DataRow("Invalid", "ActiveOwnerLeaseInvalid")]
    [DataRow("Live", "LiveLeaseWithoutAuthenticatedEndpoint")]
    public void OfflineUnsafeLeaseRequiresManualIntervention(
        string leaseStateName,
        string reasonName)
    {
        var state = Enum.Parse<ActiveOwnerLeaseState>(leaseStateName);
        var lease = state switch
        {
            ActiveOwnerLeaseState.Live =>
                LiveLease(Node(81005), "hidden-owner"),
            ActiveOwnerLeaseState.Stale =>
                StaleLease(Node(81005), "stale-owner"),
            _ => new ActiveOwnerLeaseSnapshot(state),
        };

        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.FailedSafe, requiresRollback: true),
            Offline(lease));

        AssertManual(plan, Enum.Parse<BridgeHostRecoveryReason>(reasonName));
    }

    [DataTestMethod]
    [DataRow("Missing", "AuthenticatedEndpointWithoutLiveLease")]
    [DataRow("Stale", "ActiveOwnerLeaseStale")]
    [DataRow("Invalid", "ActiveOwnerLeaseInvalid")]
    public void AuthenticatedEndpointWithoutAValidLiveLeaseRequiresManualIntervention(
        string leaseStateName,
        string reasonName)
    {
        var identity = Node(processId: 81006);

        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.FailedSafe, requiresRollback: true),
            Authenticated(
                identity,
                leaseStateName == "Stale"
                    ? StaleLease(identity, "stale-authenticated")
                    : new ActiveOwnerLeaseSnapshot(
                        Enum.Parse<ActiveOwnerLeaseState>(leaseStateName))));

        AssertManual(plan, Enum.Parse<BridgeHostRecoveryReason>(reasonName));
    }

    [TestMethod]
    public void LeaseAndEndpointMustNameTheSameOwner()
    {
        var identity = Node(processId: 81007);
        var other = Node(processId: 81008);

        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.FailedSafe, requiresRollback: true),
            Authenticated(identity, LiveLease(other, "different-owner")));

        AssertManual(plan, BridgeHostRecoveryReason.LeaseIdentityMismatch);
    }

    [DataTestMethod]
    [DataRow("wrong-instance")]
    [DataRow("passive")]
    [DataRow("wrong-api")]
    public void UnexpectedEndpointIdentityRequiresManualIntervention(string variation)
    {
        var identity = variation switch
        {
            "wrong-instance" => Node(81009) with { InstanceName = "other-node" },
            "passive" => Node(81009) with
            {
                OwnershipMode = "passive",
                ActiveOwner = false,
            },
            "wrong-api" => Node(81009) with
            {
                ManagementApiVersion =
                    BridgeHostCutoverTransaction.CurrentManagementApiVersion + 1,
            },
            _ => throw new InvalidOperationException("unknown test variation"),
        };

        var leaseIdentity = variation == "passive"
            ? Node(81009)
            : identity;
        var plan = Planner().Plan(
            Snapshot(BridgeHostCutoverStage.FailedSafe, requiresRollback: true),
            Authenticated(
                identity,
                LiveLease(leaseIdentity, "unexpected-endpoint")));

        AssertManual(plan, BridgeHostRecoveryReason.UnexpectedEndpointIdentity);
    }

    [TestMethod]
    public void EndpointObservationRejectsImpossibleIdentityCombinations()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new BridgeHostRecoveryEndpointObservation(
                BridgeHostRecoveryEndpointState.Authenticated,
                null).Validate());
        Assert.ThrowsException<InvalidOperationException>(() =>
            new BridgeHostRecoveryEndpointObservation(
                BridgeHostRecoveryEndpointState.Offline,
                Node(81010)).Validate());
    }

    [TestMethod]
    public void RecoveryObservationRejectsForgedLeaseStateCombinations()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new BridgeHostRecoveryObservation(
                BridgeHostRecoveryEndpointObservation.Offline(),
                new ActiveOwnerLeaseSnapshot(
                    ActiveOwnerLeaseState.Missing,
                    LiveLease(Node(81011), "forged-missing").Record)).Validate());
        Assert.ThrowsException<InvalidOperationException>(() =>
            new BridgeHostRecoveryObservation(
                BridgeHostRecoveryEndpointObservation.Offline(),
                new ActiveOwnerLeaseSnapshot(
                    ActiveOwnerLeaseState.Live,
                    null)).Validate());
        Assert.ThrowsException<InvalidOperationException>(() =>
            new BridgeHostRecoveryObservation(
                BridgeHostRecoveryEndpointObservation.Offline(),
                new ActiveOwnerLeaseSnapshot(
                    ActiveOwnerLeaseState.Live,
                    LiveLease(Node(81011), "forged-live").Record! with
                    {
                        SchemaVersion = ActiveOwnerLeaseObserver.SchemaVersion + 1,
                    })).Validate());
    }

    [TestMethod]
    public void PublishedPlanContainsNoProcessOrPathDetails()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Disposition",
                "HasAutomaticSteps",
                "Reason",
                "RequiresManualIntervention",
                "Steps",
            },
            typeof(BridgeHostRecoveryPlan)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [TestMethod]
    public void PlannerRejectsUnsafeInstanceNames()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new BridgeHostRecoveryPlanner("生产", DotNetInstanceName));
        Assert.ThrowsException<ArgumentException>(() =>
            new BridgeHostRecoveryPlanner(NodeInstanceName, "../dotnet"));
    }

    [DataTestMethod]
    [DataRow("Completed", true, "None")]
    [DataRow("NodeOfflineVerified", false, "OwnershipUncertain")]
    [DataRow("RollbackRequired", false, "StoreNotFlushed")]
    [DataRow("FailedSafe", true, "None")]
    public void InvalidCheckpointNeverProducesAutomaticRecovery(
        string stageName,
        bool requiresRollback,
        string failureName)
    {
        var checkpoint = new BridgeHostCutoverSnapshot(
            Enum.Parse<BridgeHostCutoverStage>(stageName),
            requiresRollback,
            Enum.Parse<BridgeCutoverFailureReason>(failureName));

        var plan = Planner().Plan(
            checkpoint,
            Offline(new ActiveOwnerLeaseSnapshot(ActiveOwnerLeaseState.Missing)));

        AssertManual(plan, BridgeHostRecoveryReason.InvalidCheckpoint);
    }

    private static BridgeHostRecoveryPlanner Planner() =>
        new(NodeInstanceName, DotNetInstanceName);

    private static BridgeHostCutoverSnapshot Snapshot(
        BridgeHostCutoverStage stage,
        bool requiresRollback = false) =>
        new(
            stage,
            requiresRollback,
            stage is BridgeHostCutoverStage.Completed or
                BridgeHostCutoverStage.Planned or
                BridgeHostCutoverStage.NodeStopRequested or
                BridgeHostCutoverStage.NodeOfflineVerified or
                BridgeHostCutoverStage.StoreHandoffVerified or
                BridgeHostCutoverStage.DotNetStartRequested or
                BridgeHostCutoverStage.DotNetActiveVerified
                ? BridgeCutoverFailureReason.None
                : BridgeCutoverFailureReason.OwnershipUncertain);

    private static BridgeHostRecoveryObservation Offline(
        ActiveOwnerLeaseSnapshot lease) =>
        new(BridgeHostRecoveryEndpointObservation.Offline(), lease);

    private static BridgeHostRecoveryObservation Authenticated(
        BridgeCutoverHostIdentity identity,
        ActiveOwnerLeaseSnapshot lease) =>
        new(BridgeHostRecoveryEndpointObservation.Authenticated(identity), lease);

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

    private static ActiveOwnerLeaseSnapshot LiveLease(
        BridgeCutoverHostIdentity identity,
        string leaseId) =>
        new(
            ActiveOwnerLeaseState.Live,
            new ActiveOwnerLeaseRecord(
                ActiveOwnerLeaseObserver.SchemaVersion,
                identity.HostKind,
                identity.OwnershipMode,
                identity.ProcessId,
                identity.InstanceName,
                leaseId,
                DateTimeOffset.Parse("2026-08-07T12:00:00.000Z")));

    private static ActiveOwnerLeaseSnapshot StaleLease(
        BridgeCutoverHostIdentity identity,
        string leaseId) =>
        LiveLease(identity, leaseId) with
        {
            State = ActiveOwnerLeaseState.Stale,
        };

    private static void AssertSteps(
        BridgeHostRecoveryPlan plan,
        params BridgeHostRecoveryStep[] expected)
    {
        Assert.IsTrue(plan.HasAutomaticSteps);
        Assert.IsFalse(plan.RequiresManualIntervention);
        Assert.AreEqual(BridgeHostRecoveryReason.None, plan.Reason);
        CollectionAssert.AreEqual(expected, plan.Steps.ToArray());
    }

    private static void AssertManual(
        BridgeHostRecoveryPlan plan,
        BridgeHostRecoveryReason reason)
    {
        Assert.AreEqual(
            BridgeHostRecoveryDisposition.ManualIntervention,
            plan.Disposition);
        Assert.AreEqual(reason, plan.Reason);
        Assert.IsTrue(plan.RequiresManualIntervention);
        Assert.IsFalse(plan.HasAutomaticSteps);
        Assert.AreEqual(0, plan.Steps.Count);
    }
}
