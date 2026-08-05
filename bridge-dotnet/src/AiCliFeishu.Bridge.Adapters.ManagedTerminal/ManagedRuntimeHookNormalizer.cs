using System.Text.Json;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class ManagedRuntimeHookNormalizer(
    Func<string>? eventIdFactory = null,
    Func<DateTimeOffset>? clock = null,
    int deduplicationCapacity = 1_024)
{
    private readonly Func<string> nextEventId = eventIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Func<DateTimeOffset> utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Queue<string> recentFingerprints = new();
    private readonly HashSet<string> fingerprintSet = new(StringComparer.Ordinal);
    private readonly object fingerprintLock = new();
    private readonly int capacity = Math.Max(1, deduplicationCapacity);

    public RuntimeEventEnvelope? Normalize(
        JsonElement hook,
        string traceId,
        bool deduplicate = true)
    {
        if (hook.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(traceId))
        {
            return null;
        }
        var runtime = OptionalString(hook, "runtime") ?? RuntimeNames.Codex;
        if (runtime is not RuntimeNames.Codex and not RuntimeNames.ClaudeCode)
        {
            return null;
        }
        var sessionId = OptionalString(hook, "session_id");
        var eventName = OptionalString(hook, "hook_event_name");
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(eventName))
        {
            return null;
        }

        var normalized = NormalizePayload(eventName, hook);
        if (normalized is null)
        {
            return null;
        }
        var runtimeEvent = new RuntimeEventEnvelope
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            EventId = nextEventId(),
            EventType = normalized.Value.EventType,
            OccurredAt = utcNow().ToUniversalTime().ToString("O"),
            Runtime = runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = sessionId,
                Cwd = OptionalString(hook, "cwd"),
            },
            TraceId = traceId,
            CorrelationId = normalized.Value.CorrelationId,
            Payload = normalized.Value.Payload,
        };
        if (!BridgeProtocolValidator.Validate(runtimeEvent).IsValid)
        {
            return null;
        }
        if (!deduplicate)
        {
            return runtimeEvent;
        }
        var fingerprint = $"{runtime}:{hook.GetRawText()}";
        return TryRemember(fingerprint) ? runtimeEvent : null;
    }

    private static NormalizedHook? NormalizePayload(string eventName, JsonElement hook)
    {
        var turnId = OptionalString(hook, "turn_id");
        return eventName switch
        {
            "SessionStart" => Event(
                RuntimeEventTypes.SessionStarted,
                OptionalObject(("model", OptionalString(hook, "model")))),
            "SessionEnd" => Event(
                RuntimeEventTypes.SessionEnded,
                OptionalObject(("reason", OptionalString(hook, "reason")))),
            "PermissionRequest" => NormalizeApproval(hook),
            "PreToolUse" when OptionalString(hook, "tool_name") == "request_user_input" =>
                NormalizeInput(hook),
            "UserPromptSubmit" => Event(
                RuntimeEventTypes.TurnStarted,
                OptionalObject(("turnId", turnId))),
            "PreToolUse" or "PostToolUse" or "PreCompact" or "PostCompact" => Event(
                RuntimeEventTypes.TurnActivity,
                OptionalObject(
                    ("turnId", turnId),
                    ("summary", ActivitySummary(eventName, hook)))),
            "PostToolUseFailure" => Event(
                RuntimeEventTypes.TurnFailed,
                OptionalObject(
                    ("turnId", turnId),
                    ("error", OptionalString(hook, "tool_response_preview") ?? "工具执行失败"))),
            "Stop" => Event(
                RuntimeEventTypes.TurnCompleted,
                OptionalObject(
                    ("turnId", turnId),
                    ("message", OptionalString(hook, "last_assistant_message")))),
            _ => null,
        };
    }

    private static NormalizedHook? NormalizeApproval(JsonElement hook)
    {
        var requestId = OptionalString(hook, "tool_use_id") ?? OptionalString(hook, "turn_id");
        var toolName = OptionalString(hook, "tool_name");
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }
        string? description = null;
        if (hook.TryGetProperty("tool_input", out var toolInput))
        {
            description = toolInput.GetRawText();
            if (description.Length > 1_200)
            {
                description = $"{description[..1_180]}…（已截断）";
            }
        }
        return new(
            RuntimeEventTypes.ApprovalRequested,
            JsonSerializer.SerializeToElement(new
            {
                requestId,
                title = toolName,
                description,
            }),
            requestId);
    }

    private static NormalizedHook? NormalizeInput(JsonElement hook)
    {
        var requestId = OptionalString(hook, "tool_use_id") ?? OptionalString(hook, "turn_id");
        if (string.IsNullOrWhiteSpace(requestId) ||
            !hook.TryGetProperty("tool_input", out var input) ||
            input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var normalizedQuestions = new List<object>();
        foreach (var question in questions.EnumerateArray())
        {
            var id = OptionalString(question, "id");
            var prompt = OptionalString(question, "question");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }
            string[]? options = null;
            if (question.TryGetProperty("options", out var optionsElement) &&
                optionsElement.ValueKind == JsonValueKind.Array)
            {
                options = optionsElement.EnumerateArray()
                    .Select(option => OptionalString(option, "label"))
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Cast<string>()
                    .ToArray();
            }
            normalizedQuestions.Add(new
            {
                id,
                prompt,
                options,
                multiple = OptionalBoolean(question, "multiple"),
            });
        }
        if (normalizedQuestions.Count == 0)
        {
            return null;
        }
        return new(
            RuntimeEventTypes.InputRequested,
            JsonSerializer.SerializeToElement(new { requestId, questions = normalizedQuestions }),
            requestId);
    }

    private static string ActivitySummary(string eventName, JsonElement hook)
    {
        var toolName = OptionalString(hook, "tool_name");
        return toolName is null ? eventName : $"{eventName}: {toolName}";
    }

    private static NormalizedHook Event(string eventType, JsonElement payload) =>
        new(eventType, payload, null);

    private static JsonElement OptionalObject(params (string Name, object? Value)[] values)
    {
        var result = values
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(result);
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool OptionalBoolean(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            property.GetBoolean();
    }

    private bool TryRemember(string fingerprint)
    {
        lock (fingerprintLock)
        {
            if (!fingerprintSet.Add(fingerprint))
            {
                return false;
            }
            recentFingerprints.Enqueue(fingerprint);
            while (recentFingerprints.Count > capacity)
            {
                fingerprintSet.Remove(recentFingerprints.Dequeue());
            }
            return true;
        }
    }

    private readonly record struct NormalizedHook(
        string EventType,
        JsonElement Payload,
        string? CorrelationId);
}
