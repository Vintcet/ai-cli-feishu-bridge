using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

internal enum BridgeHostRecoveryEndpointState
{
    Offline,
    Authenticated,
    Uncertain,
}

internal enum BridgeHostRecoveryDisposition
{
    NodeAlreadyActive,
    DotNetAlreadyActive,
    RestartNode,
    RestartDotNet,
    RollBackDotNetToNode,
    ManualIntervention,
}

internal enum BridgeHostRecoveryReason
{
    None,
    InvalidCheckpoint,
    CheckpointMissing,
    CheckpointUnavailable,
    CheckpointChanged,
    ObservationChanged,
    EndpointUncertain,
    ActiveOwnerLeaseInvalid,
    ActiveOwnerLeaseStale,
    LiveLeaseWithoutAuthenticatedEndpoint,
    AuthenticatedEndpointWithoutLiveLease,
    LeaseIdentityMismatch,
    UnexpectedEndpointIdentity,
    UnexpectedCommittedOwner,
    RecoveryTargetUnbound,
}

internal enum BridgeHostRecoveryStep
{
    RequestDotNetStop,
    VerifyDotNetOffline,
    InspectStoreHandoff,
    StartNode,
    VerifyNodeActive,
    StartDotNet,
    VerifyDotNetActive,
}

internal sealed record BridgeHostRecoveryEndpointObservation(
    BridgeHostRecoveryEndpointState State,
    BridgeCutoverHostIdentity? Identity)
{
    public static BridgeHostRecoveryEndpointObservation Offline() =>
        new(BridgeHostRecoveryEndpointState.Offline, null);

    public static BridgeHostRecoveryEndpointObservation Uncertain() =>
        new(BridgeHostRecoveryEndpointState.Uncertain, null);

    public static BridgeHostRecoveryEndpointObservation Authenticated(
        BridgeCutoverHostIdentity identity) =>
        new(
            BridgeHostRecoveryEndpointState.Authenticated,
            identity ?? throw new ArgumentNullException(nameof(identity)));

    public BridgeHostRecoveryEndpointObservation Validate()
    {
        if (!Enum.IsDefined(State))
        {
            throw new InvalidOperationException("恢复端点状态无效。");
        }
        if (State is BridgeHostRecoveryEndpointState.Authenticated && Identity is null)
        {
            throw new InvalidOperationException(
                "已认证的恢复端点必须携带 Bridge Host 身份。");
        }
        if (State is not BridgeHostRecoveryEndpointState.Authenticated && Identity is not null)
        {
            throw new InvalidOperationException(
                "离线或不确定的恢复端点不能携带已认证身份。");
        }
        return this;
    }
}

internal sealed record BridgeHostRecoveryObservation(
    BridgeHostRecoveryEndpointObservation Endpoint,
    ActiveOwnerLeaseSnapshot Lease)
{
    public BridgeHostRecoveryObservation Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentNullException.ThrowIfNull(Lease);
        _ = Endpoint.Validate();
        if (!Enum.IsDefined(Lease.State))
        {
            throw new InvalidOperationException("Active Owner 租约状态无效。");
        }
        if (Lease.State is ActiveOwnerLeaseState.Live or ActiveOwnerLeaseState.Stale)
        {
            if (!ActiveOwnerLeaseObserver.IsValidRecord(Lease.Record))
            {
                throw new InvalidOperationException(
                    "在线或残留的 Active Owner 租约必须携带有效身份。");
            }
        }
        else if (Lease.Record is not null)
        {
            throw new InvalidOperationException(
                "缺失或无效的 Active Owner 租约不能携带 Owner 身份。");
        }
        return this;
    }
}

internal sealed record BridgeHostRecoveryPlan(
    BridgeHostRecoveryDisposition Disposition,
    BridgeHostRecoveryReason Reason)
{
    private static readonly IReadOnlyList<BridgeHostRecoveryStep> RestartNodeSteps =
        Array.AsReadOnly(new[]
        {
            BridgeHostRecoveryStep.InspectStoreHandoff,
            BridgeHostRecoveryStep.StartNode,
            BridgeHostRecoveryStep.VerifyNodeActive,
        });

    private static readonly IReadOnlyList<BridgeHostRecoveryStep> RestartDotNetSteps =
        Array.AsReadOnly(new[]
        {
            BridgeHostRecoveryStep.InspectStoreHandoff,
            BridgeHostRecoveryStep.StartDotNet,
            BridgeHostRecoveryStep.VerifyDotNetActive,
        });

    private static readonly IReadOnlyList<BridgeHostRecoveryStep> RollBackDotNetSteps =
        Array.AsReadOnly(new[]
        {
            BridgeHostRecoveryStep.RequestDotNetStop,
            BridgeHostRecoveryStep.VerifyDotNetOffline,
            BridgeHostRecoveryStep.InspectStoreHandoff,
            BridgeHostRecoveryStep.StartNode,
            BridgeHostRecoveryStep.VerifyNodeActive,
        });

    public IReadOnlyList<BridgeHostRecoveryStep> Steps => Disposition switch
    {
        BridgeHostRecoveryDisposition.RestartNode => RestartNodeSteps,
        BridgeHostRecoveryDisposition.RestartDotNet => RestartDotNetSteps,
        BridgeHostRecoveryDisposition.RollBackDotNetToNode => RollBackDotNetSteps,
        _ => Array.Empty<BridgeHostRecoveryStep>(),
    };

    public bool RequiresManualIntervention =>
        Disposition is BridgeHostRecoveryDisposition.ManualIntervention;

    public bool HasAutomaticSteps => Steps.Count > 0;
}

internal sealed class BridgeHostRecoveryPlanner
{
    private readonly string expectedNodeInstanceName;
    private readonly string expectedDotNetInstanceName;

    public BridgeHostRecoveryPlanner(
        string expectedNodeInstanceName,
        string expectedDotNetInstanceName)
    {
        if (!IsAsciiToken(expectedNodeInstanceName))
        {
            throw new ArgumentException(
                "Node 恢复实例名只能包含 ASCII 字母、数字、连字符和下划线。",
                nameof(expectedNodeInstanceName));
        }
        if (!IsAsciiToken(expectedDotNetInstanceName))
        {
            throw new ArgumentException(
                ".NET 恢复实例名只能包含 ASCII 字母、数字、连字符和下划线。",
                nameof(expectedDotNetInstanceName));
        }
        this.expectedNodeInstanceName = expectedNodeInstanceName;
        this.expectedDotNetInstanceName = expectedDotNetInstanceName;
    }

    public BridgeHostRecoveryPlan Plan(
        BridgeHostCutoverSnapshot checkpoint,
        BridgeHostRecoveryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!IsValidCheckpoint(checkpoint))
        {
            return Manual(BridgeHostRecoveryReason.InvalidCheckpoint);
        }
        observation = (observation ?? throw new ArgumentNullException(nameof(observation)))
            .Validate();

        if (observation.Endpoint.State is BridgeHostRecoveryEndpointState.Uncertain)
        {
            return Manual(BridgeHostRecoveryReason.EndpointUncertain);
        }

        if (observation.Endpoint.State is BridgeHostRecoveryEndpointState.Offline)
        {
            return PlanForOfflineEndpoint(checkpoint, observation.Lease);
        }

        return PlanForAuthenticatedEndpoint(
            checkpoint,
            observation.Endpoint.Identity!,
            observation.Lease);
    }

    private BridgeHostRecoveryPlan PlanForOfflineEndpoint(
        BridgeHostCutoverSnapshot checkpoint,
        ActiveOwnerLeaseSnapshot lease) =>
        lease.State switch
        {
            ActiveOwnerLeaseState.Missing => checkpoint.Stage is BridgeHostCutoverStage.Completed
                ? Automatic(BridgeHostRecoveryDisposition.RestartDotNet)
                : Automatic(BridgeHostRecoveryDisposition.RestartNode),
            ActiveOwnerLeaseState.Stale =>
                Manual(BridgeHostRecoveryReason.ActiveOwnerLeaseStale),
            ActiveOwnerLeaseState.Invalid =>
                Manual(BridgeHostRecoveryReason.ActiveOwnerLeaseInvalid),
            ActiveOwnerLeaseState.Live =>
                Manual(BridgeHostRecoveryReason.LiveLeaseWithoutAuthenticatedEndpoint),
            _ => Manual(BridgeHostRecoveryReason.ActiveOwnerLeaseInvalid),
        };

    private BridgeHostRecoveryPlan PlanForAuthenticatedEndpoint(
        BridgeHostCutoverSnapshot checkpoint,
        BridgeCutoverHostIdentity identity,
        ActiveOwnerLeaseSnapshot lease)
    {
        var owner = ClassifyExpectedOwner(identity);
        if (owner is null)
        {
            return Manual(BridgeHostRecoveryReason.UnexpectedEndpointIdentity);
        }
        if (lease.State is not ActiveOwnerLeaseState.Live || lease.Record is null)
        {
            return Manual(lease.State switch
            {
                ActiveOwnerLeaseState.Stale =>
                    BridgeHostRecoveryReason.ActiveOwnerLeaseStale,
                ActiveOwnerLeaseState.Invalid =>
                    BridgeHostRecoveryReason.ActiveOwnerLeaseInvalid,
                _ => BridgeHostRecoveryReason.AuthenticatedEndpointWithoutLiveLease,
            });
        }
        if (!LeaseMatchesEndpoint(lease.Record, identity))
        {
            return Manual(BridgeHostRecoveryReason.LeaseIdentityMismatch);
        }

        var dotNetCommitted = checkpoint.Stage is BridgeHostCutoverStage.Completed;
        return (dotNetCommitted, owner.Value) switch
        {
            (false, BridgeHostRecoveryOwner.Node) =>
                Automatic(BridgeHostRecoveryDisposition.NodeAlreadyActive),
            (false, BridgeHostRecoveryOwner.DotNet) =>
                Automatic(BridgeHostRecoveryDisposition.RollBackDotNetToNode),
            (true, BridgeHostRecoveryOwner.DotNet) =>
                Automatic(BridgeHostRecoveryDisposition.DotNetAlreadyActive),
            (true, BridgeHostRecoveryOwner.Node) =>
                Manual(BridgeHostRecoveryReason.UnexpectedCommittedOwner),
            _ => Manual(BridgeHostRecoveryReason.UnexpectedEndpointIdentity),
        };
    }

    private BridgeHostRecoveryOwner? ClassifyExpectedOwner(
        BridgeCutoverHostIdentity identity)
    {
        if (identity.IsNodeActive(BridgeHostCutoverTransaction.CurrentManagementApiVersion) &&
            string.Equals(
                identity.InstanceName,
                expectedNodeInstanceName,
                StringComparison.Ordinal))
        {
            return BridgeHostRecoveryOwner.Node;
        }
        if (identity.IsDotNetActive(BridgeHostCutoverTransaction.CurrentManagementApiVersion) &&
            string.Equals(
                identity.InstanceName,
                expectedDotNetInstanceName,
                StringComparison.Ordinal))
        {
            return BridgeHostRecoveryOwner.DotNet;
        }
        return null;
    }

    private static bool LeaseMatchesEndpoint(
        ActiveOwnerLeaseRecord lease,
        BridgeCutoverHostIdentity identity) =>
        lease.ProcessId == identity.ProcessId &&
        string.Equals(lease.HostKind, identity.HostKind, StringComparison.Ordinal) &&
        string.Equals(lease.OwnershipMode, identity.OwnershipMode, StringComparison.Ordinal) &&
        string.Equals(lease.InstanceName, identity.InstanceName, StringComparison.Ordinal);

    private static BridgeHostRecoveryPlan Automatic(
        BridgeHostRecoveryDisposition disposition) =>
        new(disposition, BridgeHostRecoveryReason.None);

    private static BridgeHostRecoveryPlan Manual(BridgeHostRecoveryReason reason) =>
        new(BridgeHostRecoveryDisposition.ManualIntervention, reason);

    private static bool IsValidCheckpoint(BridgeHostCutoverSnapshot checkpoint)
    {
        if (!Enum.IsDefined(checkpoint.Stage) ||
            !Enum.IsDefined(checkpoint.FailureReason) ||
            checkpoint.FailureReason is BridgeCutoverFailureReason.InvalidEventOrder)
        {
            return false;
        }

        var hasFailure = checkpoint.FailureReason is not BridgeCutoverFailureReason.None;
        return checkpoint.Stage switch
        {
            BridgeHostCutoverStage.Planned or
            BridgeHostCutoverStage.NodeStopRequested or
            BridgeHostCutoverStage.NodeOfflineVerified or
            BridgeHostCutoverStage.StoreHandoffVerified or
            BridgeHostCutoverStage.DotNetStartRequested or
            BridgeHostCutoverStage.DotNetActiveVerified or
            BridgeHostCutoverStage.Completed =>
                !checkpoint.RequiresRollback && !hasFailure,
            BridgeHostCutoverStage.RollbackRequired or
            BridgeHostCutoverStage.DotNetStopRequested or
            BridgeHostCutoverStage.DotNetOfflineVerified or
            BridgeHostCutoverStage.NodeRollbackStartRequested =>
                checkpoint.RequiresRollback && hasFailure,
            BridgeHostCutoverStage.RolledBack =>
                !checkpoint.RequiresRollback && hasFailure,
            BridgeHostCutoverStage.FailedSafe => hasFailure,
            _ => false,
        };
    }

    private static bool IsAsciiToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private enum BridgeHostRecoveryOwner
    {
        Node,
        DotNet,
    }
}
