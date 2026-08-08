using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCliFeishu.Bridge.Protocol;

public static class BridgeProtocolVersion
{
    public const int Current = 1;
}

public static class RuntimeNames
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claudecode";
    public const string OpenCode = "opencode";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Codex, ClaudeCode, OpenCode],
        StringComparer.Ordinal);
}

public static class RuntimeCommandTypes
{
    public const string PromptSend = "prompt.send";
    public const string ApprovalResolve = "approval.resolve";
    public const string InputResolve = "input.resolve";
    public const string SessionLaunch = "session.launch";
    public const string SessionResume = "session.resume";
    public const string SessionStop = "session.stop";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [PromptSend, ApprovalResolve, InputResolve, SessionLaunch, SessionResume, SessionStop],
        StringComparer.Ordinal);
}

public static class RuntimeEventTypes
{
    public const string SessionStarted = "session.started";
    public const string SessionEnded = "session.ended";
    public const string TurnStarted = "turn.started";
    public const string TurnActivity = "turn.activity";
    public const string TurnCompleted = "turn.completed";
    public const string TurnFailed = "turn.failed";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalResolvedExternally = "approval.resolved_externally";
    public const string InputRequested = "input.requested";
    public const string InputResolvedExternally = "input.resolved_externally";
    public const string RuntimeConnected = "runtime.connected";
    public const string RuntimeDisconnected = "runtime.disconnected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [
            SessionStarted, SessionEnded, TurnStarted, TurnActivity,
            TurnCompleted, TurnFailed, ApprovalRequested,
            ApprovalResolvedExternally, InputRequested,
            InputResolvedExternally, RuntimeConnected, RuntimeDisconnected,
        ],
        StringComparer.Ordinal);
}

public sealed record RuntimeSessionReference
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }
}

public abstract record RuntimeEnvelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = string.Empty;

    [JsonPropertyName("session")]
    public RuntimeSessionReference? Session { get; init; } = new();

    [JsonPropertyName("traceId")]
    public string TraceId { get; init; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }
}

public sealed record RuntimeCommandEnvelope : RuntimeEnvelope
{
    [JsonPropertyName("commandId")]
    public string CommandId { get; init; } = string.Empty;

    [JsonPropertyName("commandType")]
    public string CommandType { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

public sealed record RuntimeEventEnvelope : RuntimeEnvelope
{
    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("occurredAt")]
    public string OccurredAt { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

public static class BridgeProtocolJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static RuntimeCommandEnvelope DeserializeCommand(string json)
    {
        return JsonSerializer.Deserialize<RuntimeCommandEnvelope>(
            json,
            SerializerOptions) ?? throw new JsonException("运行时命令不能为空。");
    }

    public static RuntimeEventEnvelope DeserializeEvent(string json)
    {
        return JsonSerializer.Deserialize<RuntimeEventEnvelope>(
            json,
            SerializerOptions) ?? throw new JsonException("运行时事件不能为空。");
    }
}

/// <summary>
/// Stable, CLI-neutral labels for the small amount of structured activity that
/// can be shown in a Feishu progress card.  Adapters may omit the label when a
/// runtime only provides a human-readable summary; the summary remains the
/// required compatibility field.
/// </summary>
public static class RuntimeActivityKinds
{
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string ToolFailed = "tool.failed";
    public const string ContextCompacting = "context.compacting";
    public const string ContextCompacted = "context.compacted";
    public const string PromptSubmitted = "prompt.submitted";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [
            ToolStarted,
            ToolCompleted,
            ToolFailed,
            ContextCompacting,
            ContextCompacted,
            PromptSubmitted,
        ],
        StringComparer.Ordinal);
}
