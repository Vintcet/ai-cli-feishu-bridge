namespace AiCliFeishuControl;

internal enum BridgeHostCutoverStage
{
    Planned,
    NodeStopRequested,
    NodeOfflineVerified,
    StoreHandoffVerified,
    DotNetStartRequested,
    DotNetActiveVerified,
    Completed,
    RollbackRequired,
    DotNetStopRequested,
    DotNetOfflineVerified,
    NodeRollbackStartRequested,
    RolledBack,
    FailedSafe,
}

internal enum BridgeCutoverLeaseState
{
    Missing,
    Stale,
    Live,
    Invalid,
}

internal enum BridgeCutoverFailureReason
{
    None,
    InvalidEventOrder,
    UnexpectedFailure,
    NodeIdentityMismatch,
    NodeStillOnline,
    StoreNotFlushed,
    StoreIncompatible,
    ActiveOwnerLive,
    ActiveOwnerInvalid,
    DotNetStartInvalidProcess,
    DotNetIdentityMismatch,
    DotNetStillOnline,
    NodeRollbackStartInvalidProcess,
    NodeRollbackIdentityMismatch,
}

internal sealed record BridgeCutoverHostIdentity(
    int ProcessId,
    string HostKind,
    int ManagementApiVersion,
    string OwnershipMode,
    bool ActiveOwner,
    string InstanceName)
{
    public bool IsNodeActive(int expectedManagementApiVersion) =>
        ProcessId > 0 &&
        ManagementApiVersion == expectedManagementApiVersion &&
        string.Equals(HostKind, "node", StringComparison.Ordinal) &&
        string.Equals(OwnershipMode, "active", StringComparison.Ordinal) &&
        ActiveOwner;

    public bool IsDotNetActive(int expectedManagementApiVersion) =>
        ProcessId > 0 &&
        ManagementApiVersion == expectedManagementApiVersion &&
        string.Equals(HostKind, "dotnet", StringComparison.Ordinal) &&
        string.Equals(OwnershipMode, "active", StringComparison.Ordinal) &&
        ActiveOwner;

    public bool Matches(BridgeCutoverHostIdentity other) =>
        ProcessId == other.ProcessId &&
        ManagementApiVersion == other.ManagementApiVersion &&
        ActiveOwner == other.ActiveOwner &&
        string.Equals(HostKind, other.HostKind, StringComparison.Ordinal) &&
        string.Equals(OwnershipMode, other.OwnershipMode, StringComparison.Ordinal) &&
        string.Equals(InstanceName, other.InstanceName, StringComparison.Ordinal);
}

internal sealed record BridgeStoreHandoffEvidence(
    bool StoreFlushed,
    bool StoreCompatible,
    BridgeCutoverLeaseState LeaseState)
{
    public bool CanHandoff =>
        StoreFlushed &&
        StoreCompatible &&
        LeaseState is BridgeCutoverLeaseState.Missing or BridgeCutoverLeaseState.Stale;
}

internal abstract record BridgeHostCutoverEvent;

internal sealed record NodeStopRequestedEvent(
    BridgeCutoverHostIdentity Identity) : BridgeHostCutoverEvent;

internal sealed record NodeOfflineVerifiedEvent(
    int ProcessId) : BridgeHostCutoverEvent;

internal sealed record StoreHandoffVerifiedEvent(
    BridgeStoreHandoffEvidence Evidence) : BridgeHostCutoverEvent;

internal sealed record DotNetStartRequestedEvent(
    int ProcessId) : BridgeHostCutoverEvent;

internal sealed record DotNetActiveVerifiedEvent(
    BridgeCutoverHostIdentity Identity) : BridgeHostCutoverEvent;

internal sealed record DotNetActiveVerificationFailedEvent(
    BridgeCutoverFailureReason Reason) : BridgeHostCutoverEvent;

internal sealed record CutoverCompletedEvent : BridgeHostCutoverEvent;

internal sealed record DotNetStopRequestedEvent : BridgeHostCutoverEvent;

internal sealed record DotNetOfflineVerifiedEvent(
    int ProcessId) : BridgeHostCutoverEvent;

internal sealed record NodeRollbackStartRequestedEvent(
    int ProcessId) : BridgeHostCutoverEvent;

internal sealed record NodeRollbackActiveVerifiedEvent(
    BridgeCutoverHostIdentity Identity) : BridgeHostCutoverEvent;

internal sealed record CutoverFailedEvent(
    BridgeCutoverFailureReason Reason) : BridgeHostCutoverEvent;

internal sealed record BridgeHostCutoverSnapshot(
    BridgeHostCutoverStage Stage,
    bool RequiresRollback,
    BridgeCutoverFailureReason FailureReason)
{
    public bool IsTerminal =>
        Stage is BridgeHostCutoverStage.Completed or
            BridgeHostCutoverStage.RolledBack or
            BridgeHostCutoverStage.FailedSafe;
}

internal sealed record BridgeHostCutoverApplyResult(
    bool Accepted,
    bool Changed,
    BridgeHostCutoverSnapshot Snapshot,
    BridgeCutoverFailureReason Reason);

internal sealed class BridgeHostCutoverTransaction
{
    public const int CurrentManagementApiVersion = BridgeHostTarget.CurrentManagementApiVersion;

    private readonly BridgeCutoverHostIdentity expectedNode;
    private readonly string expectedDotNetInstanceName;
    private int dotNetProcessId;
    private int nodeRollbackProcessId;
    private BridgeHostCutoverSnapshot snapshot = new(
        BridgeHostCutoverStage.Planned,
        RequiresRollback: false,
        BridgeCutoverFailureReason.None);

    private BridgeHostCutoverTransaction(
        BridgeCutoverHostIdentity expectedNode,
        string expectedDotNetInstanceName)
    {
        this.expectedNode = expectedNode;
        this.expectedDotNetInstanceName = expectedDotNetInstanceName;
    }

    public BridgeHostCutoverSnapshot Snapshot => snapshot;

    public static BridgeHostCutoverTransaction Create(
        BridgeCutoverHostIdentity expectedNode,
        string expectedDotNetInstanceName)
    {
        ArgumentNullException.ThrowIfNull(expectedNode);
        if (!expectedNode.IsNodeActive(CurrentManagementApiVersion))
        {
            throw new ArgumentException(
                "切换事务必须从已认证的 Node Active 身份开始。",
                nameof(expectedNode));
        }
        if (!IsValidInstanceName(expectedDotNetInstanceName))
        {
            throw new ArgumentException(
                ".NET Active 实例名只能包含字母、数字、连字符和下划线。",
                nameof(expectedDotNetInstanceName));
        }
        return new(expectedNode, expectedDotNetInstanceName);
    }

    public BridgeHostCutoverApplyResult Apply(BridgeHostCutoverEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            NodeStopRequestedEvent value => ApplyNodeStopRequested(value),
            NodeOfflineVerifiedEvent value => ApplyNodeOfflineVerified(value),
            StoreHandoffVerifiedEvent value => ApplyStoreHandoffVerified(value),
            DotNetStartRequestedEvent value => ApplyDotNetStartRequested(value),
            DotNetActiveVerifiedEvent value => ApplyDotNetActiveVerified(value),
            DotNetActiveVerificationFailedEvent value =>
                ApplyDotNetActiveVerificationFailed(value),
            CutoverCompletedEvent => ApplyCutoverCompleted(),
            DotNetStopRequestedEvent => ApplyDotNetStopRequested(),
            DotNetOfflineVerifiedEvent value => ApplyDotNetOfflineVerified(value),
            NodeRollbackStartRequestedEvent value => ApplyNodeRollbackStartRequested(value),
            NodeRollbackActiveVerifiedEvent value => ApplyNodeRollbackActiveVerified(value),
            CutoverFailedEvent value => ApplyCutoverFailed(value),
            _ => Reject(BridgeCutoverFailureReason.InvalidEventOrder),
        };
    }

    private BridgeHostCutoverApplyResult ApplyNodeStopRequested(NodeStopRequestedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.NodeStopRequested)
        {
            return value.Identity.Matches(expectedNode)
                ? NoChange()
                : FailSafe(BridgeCutoverFailureReason.NodeIdentityMismatch, false);
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.Planned)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (!value.Identity.Matches(expectedNode))
        {
            return FailSafe(BridgeCutoverFailureReason.NodeIdentityMismatch, false);
        }
        return Advance(BridgeHostCutoverStage.NodeStopRequested);
    }

    private BridgeHostCutoverApplyResult ApplyNodeOfflineVerified(NodeOfflineVerifiedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.NodeOfflineVerified &&
            value.ProcessId == expectedNode.ProcessId)
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.NodeStopRequested)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (value.ProcessId != expectedNode.ProcessId)
        {
            return FailSafe(BridgeCutoverFailureReason.NodeIdentityMismatch, false);
        }
        return Advance(BridgeHostCutoverStage.NodeOfflineVerified);
    }

    private BridgeHostCutoverApplyResult ApplyStoreHandoffVerified(
        StoreHandoffVerifiedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.StoreHandoffVerified &&
            value.Evidence.CanHandoff)
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.NodeOfflineVerified)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (!value.Evidence.StoreFlushed)
        {
            return FailSafe(BridgeCutoverFailureReason.StoreNotFlushed, false);
        }
        if (!value.Evidence.StoreCompatible)
        {
            return FailSafe(BridgeCutoverFailureReason.StoreIncompatible, false);
        }
        if (value.Evidence.LeaseState is BridgeCutoverLeaseState.Live)
        {
            return FailSafe(BridgeCutoverFailureReason.ActiveOwnerLive, false);
        }
        if (value.Evidence.LeaseState is BridgeCutoverLeaseState.Invalid)
        {
            return FailSafe(BridgeCutoverFailureReason.ActiveOwnerInvalid, false);
        }
        return Advance(BridgeHostCutoverStage.StoreHandoffVerified);
    }

    private BridgeHostCutoverApplyResult ApplyDotNetStartRequested(
        DotNetStartRequestedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.DotNetStartRequested)
        {
            if (value.ProcessId == dotNetProcessId)
            {
                return NoChange();
            }
            return RequireRollback(BridgeCutoverFailureReason.DotNetIdentityMismatch);
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.StoreHandoffVerified)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (value.ProcessId <= 0)
        {
            return FailSafe(BridgeCutoverFailureReason.DotNetStartInvalidProcess, false);
        }
        dotNetProcessId = value.ProcessId;
        return Advance(BridgeHostCutoverStage.DotNetStartRequested);
    }

    private BridgeHostCutoverApplyResult ApplyDotNetActiveVerified(
        DotNetActiveVerifiedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.DotNetActiveVerified &&
            IsExpectedDotNet(value.Identity))
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.DotNetStartRequested)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (!IsExpectedDotNet(value.Identity))
        {
            return RequireRollback(BridgeCutoverFailureReason.DotNetIdentityMismatch);
        }
        return Advance(BridgeHostCutoverStage.DotNetActiveVerified);
    }

    private BridgeHostCutoverApplyResult ApplyDotNetActiveVerificationFailed(
        DotNetActiveVerificationFailedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.RollbackRequired &&
            snapshot.FailureReason == NormalizeFailure(value.Reason))
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.DotNetStartRequested)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        return RequireRollback(NormalizeFailure(value.Reason));
    }

    private BridgeHostCutoverApplyResult ApplyCutoverCompleted()
    {
        if (snapshot.Stage is BridgeHostCutoverStage.Completed)
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.DotNetActiveVerified)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        return Advance(BridgeHostCutoverStage.Completed);
    }

    private BridgeHostCutoverApplyResult ApplyDotNetStopRequested()
    {
        if (snapshot.Stage is BridgeHostCutoverStage.DotNetStopRequested)
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.RollbackRequired)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        return Advance(
            BridgeHostCutoverStage.DotNetStopRequested,
            requiresRollback: true,
            snapshot.FailureReason);
    }

    private BridgeHostCutoverApplyResult ApplyDotNetOfflineVerified(
        DotNetOfflineVerifiedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.DotNetOfflineVerified &&
            value.ProcessId == dotNetProcessId)
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.DotNetStopRequested)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (value.ProcessId != dotNetProcessId)
        {
            return FailSafe(BridgeCutoverFailureReason.DotNetStillOnline, true);
        }
        return Advance(
            BridgeHostCutoverStage.DotNetOfflineVerified,
            requiresRollback: true,
            snapshot.FailureReason);
    }

    private BridgeHostCutoverApplyResult ApplyNodeRollbackStartRequested(
        NodeRollbackStartRequestedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.NodeRollbackStartRequested)
        {
            if (value.ProcessId == nodeRollbackProcessId)
            {
                return NoChange();
            }
            return FailSafe(BridgeCutoverFailureReason.NodeRollbackIdentityMismatch, true);
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.DotNetOfflineVerified)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (value.ProcessId <= 0)
        {
            return FailSafe(BridgeCutoverFailureReason.NodeRollbackStartInvalidProcess, true);
        }
        nodeRollbackProcessId = value.ProcessId;
        return Advance(
            BridgeHostCutoverStage.NodeRollbackStartRequested,
            requiresRollback: true,
            snapshot.FailureReason);
    }

    private BridgeHostCutoverApplyResult ApplyNodeRollbackActiveVerified(
        NodeRollbackActiveVerifiedEvent value)
    {
        if (snapshot.Stage is BridgeHostCutoverStage.RolledBack &&
            IsExpectedRollbackNode(value.Identity))
        {
            return NoChange();
        }
        if (snapshot.Stage is not BridgeHostCutoverStage.NodeRollbackStartRequested)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (!IsExpectedRollbackNode(value.Identity))
        {
            return FailSafe(BridgeCutoverFailureReason.NodeRollbackIdentityMismatch, true);
        }
        return Advance(
            BridgeHostCutoverStage.RolledBack,
            requiresRollback: false,
            snapshot.FailureReason);
    }

    private BridgeHostCutoverApplyResult ApplyCutoverFailed(CutoverFailedEvent value)
    {
        var reason = NormalizeFailure(value.Reason);
        if (snapshot.Stage is BridgeHostCutoverStage.RollbackRequired &&
            snapshot.FailureReason == reason)
        {
            return NoChange();
        }
        if (snapshot.Stage is BridgeHostCutoverStage.FailedSafe &&
            snapshot.FailureReason == reason)
        {
            return NoChange();
        }
        if (snapshot.IsTerminal)
        {
            return Reject(BridgeCutoverFailureReason.InvalidEventOrder);
        }
        if (snapshot.Stage is BridgeHostCutoverStage.DotNetStartRequested or
            BridgeHostCutoverStage.DotNetActiveVerified)
        {
            return RequireRollback(reason);
        }
        return FailSafe(reason, RequiresRollbackAfterFailure(snapshot.Stage));
    }

    private bool IsExpectedDotNet(BridgeCutoverHostIdentity identity) =>
        identity.ProcessId == dotNetProcessId &&
        identity.IsDotNetActive(CurrentManagementApiVersion) &&
        string.Equals(
            identity.InstanceName,
            expectedDotNetInstanceName,
            StringComparison.Ordinal);

    private bool IsExpectedRollbackNode(BridgeCutoverHostIdentity identity) =>
        identity.ProcessId == nodeRollbackProcessId &&
        identity.IsNodeActive(CurrentManagementApiVersion) &&
        string.Equals(identity.InstanceName, expectedNode.InstanceName, StringComparison.Ordinal);

    private BridgeHostCutoverApplyResult RequireRollback(
        BridgeCutoverFailureReason reason) =>
        Advance(BridgeHostCutoverStage.RollbackRequired, true, reason);

    private BridgeHostCutoverApplyResult Advance(
        BridgeHostCutoverStage stage,
        bool? requiresRollback = null,
        BridgeCutoverFailureReason? failureReason = null)
    {
        snapshot = new(
            stage,
            requiresRollback ?? snapshot.RequiresRollback,
            failureReason ?? snapshot.FailureReason);
        return new(true, true, snapshot, snapshot.FailureReason);
    }

    private BridgeHostCutoverApplyResult FailSafe(
        BridgeCutoverFailureReason reason,
        bool requiresRollback) =>
        Advance(BridgeHostCutoverStage.FailedSafe, requiresRollback, reason);

    private BridgeHostCutoverApplyResult Reject(BridgeCutoverFailureReason reason) =>
        new(false, false, snapshot, reason);

    private BridgeHostCutoverApplyResult NoChange() =>
        new(true, false, snapshot, snapshot.FailureReason);

    private static BridgeCutoverFailureReason NormalizeFailure(
        BridgeCutoverFailureReason reason) =>
        reason is BridgeCutoverFailureReason.None or
            BridgeCutoverFailureReason.InvalidEventOrder
            ? BridgeCutoverFailureReason.UnexpectedFailure
            : reason;

    private static bool RequiresRollbackAfterFailure(BridgeHostCutoverStage stage) =>
        stage is BridgeHostCutoverStage.RollbackRequired or
            BridgeHostCutoverStage.DotNetStopRequested or
            BridgeHostCutoverStage.DotNetOfflineVerified or
            BridgeHostCutoverStage.NodeRollbackStartRequested;

    private static bool IsValidInstanceName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
