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
    BridgeControlStoreStatus Store);

public sealed class BridgeControlStatusReader(
    BridgeHealthRegistry health,
    IBridgeStoreShadow storeShadow)
{
    public BridgeControlStatusSnapshot Snapshot()
    {
        var host = health.Snapshot();
        var store = storeShadow.Snapshot;
        var core = store.Core;
        var sessions = core?.Sessions.Sessions.Values;
        var approvals = core?.Approvals.Requests.Values;

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
                approvals?.Count(approval => approval.Status == ApprovalStatuses.Pending) ?? 0));
    }
}
