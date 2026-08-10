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
    private static bool TryPendingInput(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out InputRequestState input,
        out SessionState session)
    {
        if (current.Initialized &&
            current.Inputs.Requests.TryGetValue(requestId, out input!) &&
            input.Status == InputRequestStatuses.Pending &&
            string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        input = null!;
        session = null!;
        return false;
    }

    private static bool TryTerminalInput(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out InputRequestState input,
        out SessionState session)
    {
        if (current.Initialized &&
            current.Inputs.Requests.TryGetValue(requestId, out input!) &&
            IsTerminalInput(input) &&
            string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        input = null!;
        session = null!;
        return false;
    }

    private static bool IsTerminalInput(InputRequestState input) =>
        input.Status is (InputRequestStatuses.Resolved or
            InputRequestStatuses.Local or
            InputRequestStatuses.TimedOut) &&
        input.ResolvedAt is not null;

    private static FeishuInputQuestionView[] InputQuestions(InputRequestState input) =>
        input.Questions.Select((question, index) => new FeishuInputQuestionView(
            question.Id,
            string.IsNullOrWhiteSpace(question.Header)
                ? $"问题 {index + 1}"
                : question.Header,
            string.IsNullOrWhiteSpace(question.Prompt)
                ? question.Id
                : question.Prompt,
            question.Multiple,
            question.AllowsCustom,
            question.IsSecret,
            question.Options)).ToArray();

    private static List<InputRouteTarget> InputRoutes(
        BridgeStoreSnapshot store,
        InputRequestState input,
        IReadOnlyList<FeishuInputQuestionView> questions)
    {
        var indexes = questions
            .Select((question, index) => (question.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        var routes = new List<InputRouteTarget>();
        foreach (var route in store.Routes.Messages.Values.Where(route =>
                     string.Equals(route.Kind, "input", StringComparison.Ordinal) &&
                     string.Equals(route.RequestId, input.RequestId, StringComparison.Ordinal) &&
                     string.Equals(route.SessionId, input.SessionId, StringComparison.Ordinal)))
        {
            var questionId = ExtensionString(route.ExtensionData, "questionId") ??
                (questions.Count == 1 ? questions[0].Id : null);
            if (questionId is null || !indexes.TryGetValue(questionId, out var index))
            {
                continue;
            }
            routes.Add(new(
                route.ChatId,
                new(
                    route.MessageId,
                    questionId,
                    index,
                    ExtensionString(route.ExtensionData, "selectionKey") ?? route.ChatId)));
        }
        return routes;
    }

    private static BridgeStoreSnapshot AddInputRoute(
        BridgeStoreSnapshot store,
        string requestId,
        string sessionId,
        string messageId,
        string chatId,
        string questionId,
        string selectionKey,
        DateTimeOffset createdAt)
    {
        if (store.Routes.Messages.TryGetValue(messageId, out var existing))
        {
            var existingQuestionId = ExtensionString(existing.ExtensionData, "questionId");
            if (!string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(existing.RequestId, requestId, StringComparison.Ordinal) ||
                !string.Equals(existing.ChatId, chatId, StringComparison.Ordinal) ||
                !string.Equals(existing.Kind, "input", StringComparison.Ordinal) ||
                existingQuestionId is not null &&
                !string.Equals(existingQuestionId, questionId, StringComparison.Ordinal))
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
                Kind = "input",
                CreatedAt = createdAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["questionId"] = JsonSerializer.SerializeToElement(questionId),
                    ["selectionKey"] = JsonSerializer.SerializeToElement(selectionKey),
                },
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

    private static string InputNotificationKey(
        string requestId,
        string chatId,
        string questionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{requestId}\0input\0{chatId}\0{questionId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private sealed record InputRouteTarget(
        string ChatId,
        FeishuInputCardTarget Target);

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

    private static bool TryTerminal(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out ApprovalState approval,
        out SessionState session)
    {
        if (current.Initialized &&
            current.Approvals.Requests.TryGetValue(requestId, out approval!) &&
            string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
            IsTerminal(approval) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        approval = null!;
        session = null!;
        return false;
    }

    private static bool IsTerminal(ApprovalState approval) =>
        approval.Status is ApprovalStatuses.Resolved or ApprovalStatuses.Orphaned &&
        approval.Resolution is not null &&
        approval.MessageIds.Count > 0;

    private static bool TryStoredSession(
        SessionState session,
        BridgeStoreSnapshot store,
        out SessionStoreRecord storedSession)
    {
        if (store.Sessions.Sessions.TryGetValue(session.SessionId, out storedSession!) &&
            string.Equals(session.Runtime, Runtime(storedSession), StringComparison.Ordinal) &&
            string.Equals(session.Cwd, storedSession.Cwd, StringComparison.Ordinal))
        {
            return true;
        }
        storedSession = null!;
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
        BridgeStoreSnapshot store)
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
