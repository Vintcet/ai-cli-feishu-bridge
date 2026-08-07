using System.Text.Json.Nodes;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public static class FeishuIntentTypes
{
    public const string MessagePrompt = "message.prompt";
    public const string CommandMenu = "command.menu";
    public const string CommandNew = "command.new";
    public const string CommandWorkspace = "command.workspace";
    public const string CommandStatus = "command.status";
    public const string CommandSessions = "command.sessions";
    public const string CommandAliases = "command.aliases";
    public const string CommandHelp = "command.help";
    public const string ApprovalResolve = "approval.resolve";
    public const string ApprovalDeferToLocal = "approval.defer_to_local";
    public const string InputAnswer = "input.answer";
    public const string InputToggle = "input.toggle";
    public const string InputSubmit = "input.submit";
    public const string InputDeferToLocal = "input.defer_to_local";
    public const string RetryStop = "retry.stop";
    public const string RuntimeNewSelect = "runtime.new.select";
    public const string RuntimeNewSubmit = "runtime.new.submit";
    public const string RuntimeNewCancel = "runtime.new.cancel";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            MessagePrompt,
            CommandMenu,
            CommandNew,
            CommandWorkspace,
            CommandStatus,
            CommandSessions,
            CommandAliases,
            CommandHelp,
            ApprovalResolve,
            ApprovalDeferToLocal,
            InputAnswer,
            InputToggle,
            InputSubmit,
            InputDeferToLocal,
            RetryStop,
            RuntimeNewSelect,
            RuntimeNewSubmit,
            RuntimeNewCancel,
        ],
        StringComparer.Ordinal);
}

public static class FeishuCardActions
{
    public const string CommandNew = "command_new";
    public const string CommandSessions = "command_sessions";
    public const string CommandStatus = "command_status";
    public const string CommandWorkspace = "command_workspace";
    public const string CommandAliases = "command_aliases";
    public const string CommandHelp = "command_help";
    public const string RuntimeNewSelect = "runtime_new_select";
    public const string RuntimeNewSubmit = "runtime_new_submit";
    public const string RuntimeNewCancel = "runtime_new_cancel";
    public const string RetryStop = "retry_stop";
    public const string ApprovalAllow = "approval_allow";
    public const string ApprovalDeny = "approval_deny";
    public const string ApprovalDesktop = "approval_desktop";
    public const string InputAnswer = "input_answer";
    public const string InputToggle = "input_toggle";
    public const string InputSubmit = "input_submit";
    public const string InputLocal = "input_local";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            CommandNew,
            CommandSessions,
            CommandStatus,
            CommandWorkspace,
            CommandAliases,
            CommandHelp,
            RuntimeNewSelect,
            RuntimeNewSubmit,
            RuntimeNewCancel,
            RetryStop,
            ApprovalAllow,
            ApprovalDeny,
            ApprovalDesktop,
            InputAnswer,
            InputToggle,
            InputSubmit,
            InputLocal,
        ],
        StringComparer.Ordinal);
}

public sealed record FeishuAttachment(
    string Kind,
    string Key,
    string? Name = null);

public sealed record FeishuIntent(
    string EventId,
    string IntentType,
    string OperatorOpenId,
    string ChatId,
    string MessageId,
    string ChatType,
    string TraceId,
    string? Text = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    IReadOnlyList<FeishuAttachment>? Attachments = null);

public sealed record FeishuNormalizationResult(
    FeishuIntent? Intent,
    string? Error,
    bool Duplicate = false)
{
    public bool IsAccepted => Intent is not null;

    public static FeishuNormalizationResult Accepted(FeishuIntent intent) =>
        new(intent, null);

    public static FeishuNormalizationResult Rejected(string error) =>
        new(null, error);

    public static FeishuNormalizationResult AlreadyProcessed() =>
        new(null, null, true);
}

public sealed record FeishuCardView(JsonObject Content);

public sealed record FeishuRuntimeNewContext(
    string FlowId,
    string SourceMessageId,
    string ChatId);

public sealed record FeishuSessionView(
    string SessionId,
    string Runtime,
    string Label,
    string Cwd);

public sealed record FeishuApprovalView(
    string RequestId,
    string ToolName,
    string ToolPreview,
    string RiskLevel = "normal",
    string? RiskReason = null);

public sealed record FeishuInputQuestionView(
    string Id,
    string Header,
    string Question,
    bool Multiple,
    bool AllowsCustom,
    bool IsSecret,
    IReadOnlyList<string> Options);

public sealed record FeishuInputCardTarget(
    string MessageId,
    string QuestionId,
    int QuestionIndex,
    string? SelectionKey = null);

public sealed record FeishuRuntimeRetryView(
    string CycleId,
    string State,
    int Attempt,
    int MaxAttempts,
    int DelaySeconds = 0);

public sealed record FeishuCallbackResult(
    string ToastType,
    string ToastContent,
    FeishuCardView? Card = null);

public sealed record FeishuSessionGroup(
    string ChatId,
    string Name);

public sealed class FeishuInboundEnvelope(
    string eventId,
    string traceId,
    string eventType,
    JsonElement payload,
    Func<FeishuCallbackResult?, int, CancellationToken, Task> complete)
{
    private int completed;

    public string EventId { get; } = eventId;

    public string TraceId { get; } = traceId;

    public string EventType { get; } = eventType;

    public JsonElement Payload { get; } = payload.Clone();

    public Task AcknowledgeAsync(
        FeishuCallbackResult? result = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(result, 200, cancellationToken);

    public Task RejectAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync(null, 500, cancellationToken);

    private Task CompleteAsync(
        FeishuCallbackResult? result,
        int statusCode,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return Task.CompletedTask;
        }
        return complete(result, statusCode, cancellationToken);
    }
}
