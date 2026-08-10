using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActivePersistentBusinessStateOwner
{
    public async ValueTask<ApprovalState?> ExpireApprovalAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!current.Approvals.Requests.TryGetValue(requestId, out var approval) ||
                approval.Status != ApprovalStatuses.Pending)
            {
                return null;
            }

            var observedAt = clock.GetUtcNow();
            if (approval.ExpiresAt > observedAt)
            {
                return null;
            }

            var sessions = current.Sessions;
            var resolvedAt = Latest(observedAt, approval.CreatedAt);
            if (sessions.Sessions.TryGetValue(approval.SessionId, out var session))
            {
                resolvedAt = Latest(resolvedAt, session.LastSeenAt);
                sessions = SessionStateMachine.Transition(
                    sessions,
                    approval.SessionId,
                    SessionStatuses.Waiting,
                    resolvedAt);
            }
            var resolved = ApprovalStateMachine.ResolveExternally(
                current.Approvals,
                requestId,
                ApprovalResolutions.Timeout,
                resolvedAt);
            if (!resolved.Value)
            {
                return null;
            }

            var next = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Approvals = resolved.State,
            };
            await PersistAsync(next, cancellationToken);
            Volatile.Write(ref snapshot, next);
            return next.Approvals.Requests[requestId];
        }
        finally
        {
            writeGate.Release();
        }
    }


    public async ValueTask<BridgeApprovalClaim?> TryClaimApprovalAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!current.Approvals.Requests.TryGetValue(requestId, out var approval) ||
                approval.Status != ApprovalStatuses.Pending ||
                !string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) ||
                !current.Sessions.Sessions.TryGetValue(sessionId, out var session))
            {
                return null;
            }
            var claim = ApprovalStateMachine.Claim(current.Approvals, requestId);
            if (!claim.Value)
            {
                return null;
            }
            Volatile.Write(ref snapshot, current with { Approvals = claim.State });
            return new(approval, session);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask ReleaseApprovalClaimAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            var approvals = ApprovalStateMachine.ReleaseClaim(
                current.Approvals,
                requestId);
            if (!ReferenceEquals(approvals, current.Approvals))
            {
                Volatile.Write(ref snapshot, current with { Approvals = approvals });
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeApprovalDelivery?> RecordApprovalDeliveryAsync(
        string requestId,
        string sessionId,
        string messageId,
        string chatId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!current.Approvals.Requests.TryGetValue(requestId, out var approval) ||
                !string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) ||
                !current.Sessions.Sessions.TryGetValue(sessionId, out var session))
            {
                return null;
            }

            var approvals = ApprovalStateMachine.AssociateMessage(
                current.Approvals,
                requestId,
                messageId);
            var changed = !ReferenceEquals(approvals, current.Approvals);
            var next = changed
                ? current with
                {
                    Revision = current.Revision + 1,
                    Approvals = approvals,
                }
                : current;
            await storeOwner.UpdateAsync(
                store => AddApprovalRoute(
                    BridgeStoreBusinessStateMerger.Merge(
                        store,
                        next.Sessions,
                        next.Approvals,
                        next.Inputs),
                    requestId,
                    sessionId,
                    messageId,
                    chatId,
                    createdAt),
                cancellationToken);
            if (changed)
            {
                Volatile.Write(ref snapshot, next);
            }
            return new(next.Approvals.Requests[requestId], session);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeApprovalClaim?> ResolveClaimedApprovalAsync(
        string requestId,
        string sessionId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (resolution is not ApprovalResolutions.Allow and not ApprovalResolutions.Deny)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!TryClaimedApproval(
                    current,
                    requestId,
                    sessionId,
                    out var approval,
                    out var session))
            {
                return null;
            }
            var resolvedAt = Latest(
                clock.GetUtcNow(),
                approval.CreatedAt,
                session.LastSeenAt);
            var resolved = ApprovalStateMachine.ResolveClaimed(
                current.Approvals,
                requestId,
                resolution,
                resolvedAt);
            if (!resolved.Value)
            {
                Volatile.Write(ref snapshot, current with { Approvals = resolved.State });
                return null;
            }
            var sessions = SessionStateMachine.Transition(
                current.Sessions,
                sessionId,
                resolution == ApprovalResolutions.Allow
                    ? SessionStatuses.Running
                    : SessionStatuses.Waiting,
                resolvedAt);
            var next = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Approvals = resolved.State,
            };
            await PersistAsync(next, cancellationToken);
            Volatile.Write(ref snapshot, next);
            return new(
                next.Approvals.Requests[requestId],
                next.Sessions.Sessions[sessionId]);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeApprovalClaim?> DeferClaimedApprovalAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!TryClaimedApproval(
                    current,
                    requestId,
                    sessionId,
                    out var approval,
                    out var session))
            {
                return null;
            }
            var approvals = ApprovalStateMachine.ReleaseClaim(
                current.Approvals,
                requestId);
            var next = current with
            {
                Revision = current.Revision + 1,
                Approvals = approvals,
            };
            var patch = new BridgeStoreApprovalExtensionPatch(
                requestId,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["desktopApprovalRequested"] =
                        JsonSerializer.SerializeToElement(true),
                });
            await PersistAsync(
                next,
                cancellationToken,
                approvalExtensionPatch: patch);
            Volatile.Write(ref snapshot, next);
            return new(approval, session);
        }
        finally
        {
            writeGate.Release();
        }
    }


    private static BridgeStoreSnapshot AddApprovalRoute(
        BridgeStoreSnapshot store,
        string requestId,
        string sessionId,
        string messageId,
        string chatId,
        DateTimeOffset createdAt)
    {
        if (store.Routes.Messages.TryGetValue(messageId, out var existing))
        {
            if (!string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(existing.RequestId, requestId, StringComparison.Ordinal) ||
                !string.Equals(existing.ChatId, chatId, StringComparison.Ordinal) ||
                !string.Equals(existing.Kind, "approval", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"消息 {messageId} 已绑定到其他业务路由。 ");
            }
            return store;
        }

        var messages = new Dictionary<string, MessageRouteStoreRecord>(
            store.Routes.Messages,
            StringComparer.Ordinal)
        {
            [messageId] = new()
            {
                MessageId = messageId,
                SessionId = sessionId,
                RequestId = requestId,
                ChatId = chatId,
                Kind = "approval",
                CreatedAt = createdAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
            },
        };
        return store with
        {
            Routes = new()
            {
                Messages = messages,
                ProcessedInbound = store.Routes.ProcessedInbound,
                ExtensionData = store.Routes.ExtensionData,
            },
        };
    }

    private static bool TryClaimedApproval(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out ApprovalState approval,
        out SessionState session)
    {
        if (current.Approvals.Claims.Contains(requestId) &&
            current.Approvals.Requests.TryGetValue(requestId, out approval!) &&
            approval.Status == ApprovalStatuses.Pending &&
            string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        approval = null!;
        session = null!;
        return false;
    }

    private static ApprovalRegistryState CreateApproval(
        ApprovalRegistryState state,
        SessionDirectoryState sessions,
        RuntimeEventEnvelope runtimeEvent,
        DateTimeOffset occurredAt)
    {
        var requestId = PayloadString(runtimeEvent.Payload, "requestId");
        if (state.Requests.TryGetValue(requestId, out var existing))
        {
            if (existing.SessionId == runtimeEvent.Session!.ExternalId &&
                existing.Status == ApprovalStatuses.Pending)
            {
                return state;
            }
            throw new InvalidOperationException($"审批 {requestId} 已存在且语义冲突。 ");
        }
        return ApprovalStateMachine.Create(
            state,
            new ApprovalState(
                requestId,
                runtimeEvent.Session!.ExternalId,
                ApprovalStatuses.Pending,
                occurredAt,
                PayloadTimestamp(runtimeEvent.Payload, "expiresAt"),
                [],
                TurnId: runtimeEvent.CorrelationId ?? requestId,
                Cwd: sessions.Sessions[runtimeEvent.Session.ExternalId].Cwd,
                ToolName: PayloadString(runtimeEvent.Payload, "title"),
                ToolPreview: OptionalPayloadString(
                    runtimeEvent.Payload,
                    "description") ?? string.Empty));
    }

    private static ApprovalRegistryState ResolveSessionApprovals(
        ApprovalRegistryState state,
        string sessionId,
        DateTimeOffset occurredAt)
    {
        foreach (var approval in state.Requests.Values.Where(item =>
                     item.SessionId == sessionId && item.Status == ApprovalStatuses.Pending).ToArray())
        {
            state = ApprovalStateMachine.ResolveExternally(
                state,
                approval.RequestId,
                ApprovalResolutions.Local,
                occurredAt).State;
        }
        return state;
    }

    private static void EnsureApprovalSession(
        ApprovalRegistryState state,
        string requestId,
        string sessionId)
    {
        if (!state.Requests.TryGetValue(requestId, out var approval))
        {
            throw new KeyNotFoundException($"审批 {requestId} 尚未登记。 ");
        }
        if (!string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"审批 {requestId} 不属于会话 {sessionId}。 ");
        }
    }

    private static string ApprovalResolution(JsonElement payload) =>
        PayloadString(payload, "outcome") switch
        {
            "allowed" => ApprovalResolutions.Allow,
            "denied" => ApprovalResolutions.Deny,
            "cancelled" => ApprovalResolutions.Local,
            var value => throw new InvalidDataException($"不支持的外部审批结果 {value}。"),
        };

    private static string ExternalApprovalSessionStatus(JsonElement payload) =>
        PayloadString(payload, "outcome") == "allowed"
            ? SessionStatuses.Running
            : SessionStatuses.Waiting;
}
