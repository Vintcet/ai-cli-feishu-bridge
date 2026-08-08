using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal interface IBridgeActiveApprovalNotifier
{
    Task NotifyPendingAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveFeishuApprovalNotificationCoordinator(
    IBridgeActiveApprovalStateOwner stateOwner,
    IBridgeProductionStoreOwner storeOwner,
    IFeishuGateway gateway,
    IFeishuCardRenderer renderer,
    FeishuInteractionCoordinator interactions,
    IBridgeActiveSessionGroupCoordinator sessionGroups)
    : IBridgeActiveApprovalNotifier
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
            if (delivered?.Approval.Status == ApprovalStatuses.Resolved &&
                delivered.Approval.Resolution is not null)
            {
                await interactions.SynchronizeApprovalAsync(
                    delivered.Approval,
                    sessionView,
                    approvalView,
                    cancellationToken);
            }
        }
    }

    private static bool TryPending(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out ApprovalState approval,
        out SessionState session)
    {
        if (current.Initialized &&
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

    private static FeishuSessionView SessionView(
        SessionState session,
        SessionStoreRecord stored) => new(
            session.SessionId,
            session.Runtime,
            ExtensionString(stored.ExtensionData, "alias") ??
                stored.ProjectName ??
                stored.ShortId ??
                ShortId(stored.SessionId),
            session.Cwd,
            ExtensionBoolean(stored.ExtensionData, "managedByAssistant"));

    private static FeishuApprovalView ApprovalView(
        ApprovalState approval,
        NodeStoreSnapshot store)
    {
        var stored = store.Approvals.Requests.GetValueOrDefault(approval.RequestId);
        return new(
            approval.RequestId,
            approval.ToolName,
            approval.ToolPreview,
            ExtensionString(stored?.ExtensionData, "riskLevel") ?? "normal",
            ExtensionString(stored?.ExtensionData, "riskReason"));
    }

    private static string NotificationKey(string requestId, string chatId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{requestId}\0approval\0{chatId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;

    private static string? ExtensionString(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool ExtensionBoolean(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }
}
