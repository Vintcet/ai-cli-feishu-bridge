using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuApprovalNotificationCoordinator
{
    public async Task NotifyPendingAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var current = stateOwner.Snapshot;
        if (!TryPending(current, requestId, sessionId, out var approval, out var session) ||
            approval.MessageIds.Count > 0)
        {
            return;
        }

        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var storedSession) ||
            !string.Equals(session.Runtime, Runtime(storedSession), StringComparison.Ordinal) ||
            !string.Equals(session.Cwd, storedSession.Cwd, StringComparison.Ordinal))
        {
            return;
        }

        var chats = await sessionGroups.NotificationChatsAsync(
            sessionId,
            cancellationToken);
        if (chats.Count == 0)
        {
            return;
        }

        var sessionView = SessionView(session, storedSession);
        var approvalView = ApprovalView(approval, store);
        if (await TryAutoApproveAsync(
                requestId,
                store,
                chats,
                sessionView,
                approvalView,
                cancellationToken))
        {
            return;
        }
        var card = renderer.PendingApproval(sessionView, approvalView);
        foreach (var chatId in chats
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            current = stateOwner.Snapshot;
            if (!TryPending(current, requestId, sessionId, out approval, out _) ||
                approval.MessageIds.Count > 0)
            {
                return;
            }

            var messageId = await gateway.SendCardAsync(
                chatId,
                card,
                NotificationKey(requestId, chatId),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new InvalidOperationException("飞书审批卡片未返回消息 ID。");
            }

            var delivered = await stateOwner.RecordApprovalDeliveryAsync(
                requestId,
                sessionId,
                messageId,
                chatId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (delivered is not null && IsTerminal(delivered.Approval))
            {
                await interactions.SynchronizeApprovalAsync(
                    delivered.Approval,
                    sessionView,
                    approvalView,
                    cancellationToken);
            }
        }
    }

    private async Task<bool> TryAutoApproveAsync(
        string requestId,
        BridgeStoreSnapshot store,
        IReadOnlyList<string> chats,
        FeishuSessionView session,
        FeishuApprovalView approval,
        CancellationToken cancellationToken)
    {
        if (approvals is null)
        {
            return false;
        }
        var mode = BridgeAutoApproveModes.Resolve(
            store.Settings.AutoApproveMode,
            store.Settings.AutoApprove);
        if (mode == BridgeAutoApproveModes.Off ||
            !ApprovalRiskLevels.IsAutoApprovable(
                approval.RiskLevel,
                relaxed: mode == BridgeAutoApproveModes.Relaxed))
        {
            return false;
        }
        try
        {
            var result = await approvals.HandleLocalAsync(
                requestId,
                ApprovalResolutions.Allow,
                store,
                cancellationToken);
            if (!result.Ok && !result.AlreadyResolved)
            {
                return false;
            }
            if (result.Ok && store.Settings.NotifyAutoApprovals == true)
            {
                var card = renderer.ResolvedApproval(
                    session,
                    approval,
                    ApprovalResolutions.Allow,
                    ApprovalStatuses.Resolved);
                foreach (var chatId in chats
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal))
                {
                    await gateway.SendCardAsync(
                        chatId,
                        card,
                        $"{NotificationKey(requestId, chatId)}-auto",
                        cancellationToken);
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task SynchronizeAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var current = stateOwner.Snapshot;
        if (!TryTerminal(current, requestId, sessionId, out var approval, out var session))
        {
            return;
        }
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!TryStoredSession(session, store, out var storedSession))
        {
            return;
        }
        await interactions.SynchronizeApprovalAsync(
            approval,
            SessionView(session, storedSession),
            ApprovalView(approval, store),
            cancellationToken);
    }

    public async Task SynchronizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var current = stateOwner.Snapshot;
        if (!current.Initialized ||
            !current.Sessions.Sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }
        var approvals = current.Approvals.Requests.Values
            .Where(approval =>
                string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
                IsTerminal(approval))
            .ToArray();
        if (approvals.Length == 0)
        {
            return;
        }
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!TryStoredSession(session, store, out var storedSession))
        {
            return;
        }

        Exception? firstFailure = null;
        var sessionView = SessionView(session, storedSession);
        foreach (var approval in approvals)
        {
            try
            {
                await interactions.SynchronizeApprovalAsync(
                    approval,
                    sessionView,
                    ApprovalView(approval, store),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref synchronizationFailures);
                firstFailure ??= error;
            }
        }
        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }
}
