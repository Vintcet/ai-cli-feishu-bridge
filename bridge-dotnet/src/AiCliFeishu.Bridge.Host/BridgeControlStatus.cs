using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

public sealed record BridgeControlStoreStatus(
    string Status,
    int Files,
    int Bindings,
    int Sessions,
    int ActiveSessions,
    int EndedSessions,
    int Routes,
    int ProcessedInbound,
    int Approvals,
    int PendingApprovals);

public sealed record BridgeControlBusinessStatus(
    bool Initialized,
    string SourceStatus,
    long Revision,
    long RejectedFeishuIntents,
    int Sessions,
    int ActiveSessions,
    int EndedSessions,
    int Approvals,
    int PendingApprovals,
    int Inputs,
    int PendingInputs);

public sealed record BridgeControlStatusSnapshot(
    bool Ok,
    string HostKind,
    int ManagementApiVersion,
    string InstanceName,
    string Lifecycle,
    string Version,
    int ProcessId,
    string OwnershipMode,
    bool ActiveOwner,
    BridgeControlStoreStatus Store,
    BridgeControlBusinessStatus BusinessState);

public sealed class BridgeControlStatusReader(
    BridgeHealthRegistry health,
    IBridgeStoreShadow storeShadow,
    BridgeBusinessStateOwner businessStateOwner)
{
    public BridgeControlStatusSnapshot Snapshot()
    {
        var host = health.Snapshot();
        var store = storeShadow.Snapshot;
        var core = store.Core;
        var sessions = core?.Sessions.Sessions.Values;
        var approvals = core?.Approvals.Requests.Values;
        var business = businessStateOwner.Snapshot;
        var businessSessions = business.Sessions.Sessions.Values;
        var businessApprovals = business.Approvals.Requests.Values;
        var businessInputs = business.Inputs.Requests.Values;

        return new(
            host.Ok,
            host.HostKind,
            host.ManagementApiVersion,
            host.InstanceName,
            host.Status,
            host.Version,
            host.ProcessId,
            host.OwnershipMode,
            host.ActiveOwner,
            new BridgeControlStoreStatus(
                store.Status,
                store.StoreFiles,
                store.Bindings,
                sessions?.Count() ?? 0,
                sessions?.Count(session => session.Status != SessionStatuses.Ended) ?? 0,
                sessions?.Count(session => session.Status == SessionStatuses.Ended) ?? 0,
                core?.Routes.Messages.Count ?? 0,
                core?.Routes.ProcessedInbound.Count ?? 0,
                approvals?.Count() ?? 0,
                approvals?.Count(approval => approval.Status == ApprovalStatuses.Pending) ?? 0),
            new BridgeControlBusinessStatus(
                business.Initialized,
                business.SourceStatus,
                business.Revision,
                business.RejectedFeishuIntents,
                businessSessions.Count(),
                businessSessions.Count(session => session.Status != SessionStatuses.Ended),
                businessSessions.Count(session => session.Status == SessionStatuses.Ended),
                businessApprovals.Count(),
                businessApprovals.Count(approval => approval.Status == ApprovalStatuses.Pending),
                businessInputs.Count(),
                businessInputs.Count(input => input.Status == InputRequestStatuses.Pending)));
    }
}
