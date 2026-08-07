using System.Text.Json;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class ManagedRuntimeHookNormalizer(
    Func<string>? eventIdFactory = null,
    Func<DateTimeOffset>? clock = null,
    int deduplicationCapacity = 1_024,
    TimeSpan? approvalLifetime = null,
    TimeSpan? inputLifetime = null)
{
    private readonly Func<string> nextEventId = eventIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Func<DateTimeOffset> utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Queue<string> recentFingerprints = new();
    private readonly HashSet<string> fingerprintSet = new(StringComparer.Ordinal);
    private readonly object fingerprintLock = new();
    private readonly int capacity = Math.Max(1, deduplicationCapacity);
    private readonly TimeSpan approvalWindow = PositiveLifetime(
        approvalLifetime,
        nameof(approvalLifetime));
    private readonly TimeSpan inputWindow = PositiveLifetime(
        inputLifetime,
        nameof(inputLifetime));

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

        var occurredAt = utcNow().ToUniversalTime();
        var normalized = NormalizePayload(
            eventName,
            hook,
            occurredAt,
            approvalWindow,
            inputWindow);
        if (normalized is null)
        {
            return null;
        }
        var runtimeEvent = new RuntimeEventEnvelope
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            EventId = nextEventId(),
            EventType = normalized.Value.EventType,
            OccurredAt = occurredAt.ToString("O"),
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
        var fingerprint = Fingerprint(hook, runtime);
        return TryRemember(fingerprint) ? runtimeEvent : null;
    }

    public void Release(JsonElement hook)
    {
        if (hook.ValueKind is not JsonValueKind.Object)
        {
            return;
        }
        var fingerprint = Fingerprint(hook);
        lock (fingerprintLock)
        {
            if (!fingerprintSet.Remove(fingerprint))
            {
                return;
            }
            var retained = recentFingerprints
                .Where(item => !string.Equals(item, fingerprint, StringComparison.Ordinal))
                .ToArray();
            recentFingerprints.Clear();
            foreach (var item in retained)
            {
                recentFingerprints.Enqueue(item);
            }
        }
    }

    private static NormalizedHook? NormalizePayload(
        string eventName,
        JsonElement hook,
        DateTimeOffset occurredAt,
        TimeSpan approvalLifetime,
        TimeSpan inputLifetime)
    {
        var turnId = OptionalString(hook, "turn_id");
        return eventName switch
        {
            "SessionStart" => Event(
                RuntimeEventTypes.SessionStarted,
                OptionalObject(
                    ("model", OptionalString(hook, "model")),
                    ("source", OptionalString(hook, "source")),
                    ("managedTerminalId", OptionalString(hook, "managed_terminal_id")),
                    ("managedTerminalElevated", OptionalBooleanValue(
                        hook,
                        "managed_terminal_elevated")),
                    ("managedByAssistant", OptionalString(
                        hook,
                        "managed_terminal_id") is null ? null : true),
                    ("historyEligible", true))),
            "SessionEnd" => Event(
                RuntimeEventTypes.SessionEnded,
                OptionalObject(("reason", OptionalString(hook, "reason")))),
            "PermissionRequest" => NormalizeApproval(hook, occurredAt, approvalLifetime),
            "PreToolUse" when OptionalString(hook, "tool_name") == "request_user_input" =>
                NormalizeInput(hook, occurredAt, inputLifetime),
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

    private static NormalizedHook? NormalizeApproval(
        JsonElement hook,
        DateTimeOffset occurredAt,
        TimeSpan approvalLifetime)
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
                expiresAt = (occurredAt + approvalLifetime).ToString("O"),
            }),
            requestId);
    }

    private static NormalizedHook? NormalizeInput(
        JsonElement hook,
        DateTimeOffset occurredAt,
        TimeSpan inputLifetime)
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
                header = OptionalString(question, "header") ?? id,
                prompt,
                options,
                multiple = OptionalBoolean(question, "multiple"),
                allowsCustom = OptionalBooleanValue(question, "custom") ?? true,
                isSecret = OptionalBoolean(question, "isSecret"),
            });
        }
        if (normalizedQuestions.Count == 0)
        {
            return null;
        }
        var autoResolution = OptionalPositiveMilliseconds(input, "autoResolutionMs");
        var actualLifetime = autoResolution is { } requested && requested < inputLifetime
            ? requested
            : inputLifetime;
        return new(
            RuntimeEventTypes.InputRequested,
            JsonSerializer.SerializeToElement(new
            {
                requestId,
                questions = normalizedQuestions,
                expiresAt = (occurredAt + actualLifetime).ToString("O"),
            }),
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

    private static bool? OptionalBooleanValue(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static TimeSpan? OptionalPositiveMilliseconds(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var milliseconds) ||
            !double.IsFinite(milliseconds) ||
            milliseconds <= 0)
        {
            return null;
        }
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan PositiveLifetime(TimeSpan? value, string parameterName)
    {
        var actual = value ?? TimeSpan.FromMinutes(20);
        return actual > TimeSpan.Zero
            ? actual
            : throw new ArgumentOutOfRangeException(parameterName);
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

    private static string Fingerprint(JsonElement hook, string? runtime = null) =>
        $"{runtime ?? OptionalString(hook, "runtime") ?? RuntimeNames.Codex}:{hook.GetRawText()}";

    private readonly record struct NormalizedHook(
        string EventType,
        JsonElement Payload,
        string? CorrelationId);
}
