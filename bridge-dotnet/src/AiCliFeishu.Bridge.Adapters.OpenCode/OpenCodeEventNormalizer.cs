using System.Text.Json;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed class OpenCodeEventNormalizer(
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

    public RuntimeEventEnvelope? Normalize(OpenCodeRawEvent rawEvent, string traceId)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return null;
        }
        var occurredAt = utcNow().ToUniversalTime();
        var normalized = NormalizePayload(rawEvent, occurredAt, approvalWindow, inputWindow);
        if (normalized is null || string.IsNullOrWhiteSpace(normalized.Value.SessionId))
        {
            return null;
        }
        var runtimeEvent = new RuntimeEventEnvelope
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            EventId = nextEventId(),
            EventType = normalized.Value.EventType,
            OccurredAt = occurredAt.ToString("O"),
            Runtime = RuntimeNames.OpenCode,
            Session = new RuntimeSessionReference
            {
                ExternalId = normalized.Value.SessionId,
                Cwd = normalized.Value.Cwd,
            },
            TraceId = traceId,
            CorrelationId = normalized.Value.CorrelationId,
            Payload = normalized.Value.Payload,
        };
        if (!BridgeProtocolValidator.Validate(runtimeEvent).IsValid)
        {
            return null;
        }
        var fingerprint = DeduplicationFingerprint(rawEvent, normalized.Value);
        return fingerprint is null || TryRemember(fingerprint) ? runtimeEvent : null;
    }

    private static NormalizedEvent? NormalizePayload(
        OpenCodeRawEvent rawEvent,
        DateTimeOffset occurredAt,
        TimeSpan approvalLifetime,
        TimeSpan inputLifetime)
    {
        var properties = UnwrapData(rawEvent.Properties);
        var sessionId = SessionId(properties);
        return rawEvent.Type switch
        {
            "session.created" => NormalizeSessionStarted(properties),
            "session.deleted" => Event(
                RuntimeEventTypes.SessionEnded,
                sessionId,
                OptionalObject(("reason", "deleted"))),
            "session.idle" => Event(
                RuntimeEventTypes.TurnCompleted,
                sessionId,
                OptionalObject()),
            "session.error" => Event(
                RuntimeEventTypes.TurnFailed,
                sessionId,
                OptionalObject(("error", ErrorText(properties) ?? "OpenCode 会话失败"))),
            "session.compacted" => Event(
                RuntimeEventTypes.TurnActivity,
                sessionId,
                OptionalObject(("summary", "会话上下文已压缩"))),
            "session.status" => NormalizeStatus(properties),
            "permission.asked" or "permission.v2.asked" or "permission.updated" =>
                NormalizePermissionRequested(properties, occurredAt, approvalLifetime),
            "permission.replied" or "permission.v2.replied" =>
                NormalizePermissionResolved(properties),
            "question.asked" => NormalizeQuestionAsked(properties, occurredAt, inputLifetime),
            "question.replied" or "question.rejected" => NormalizeQuestionResolved(properties),
            "message.part.updated" => NormalizeMessagePart(properties),
            _ => null,
        };
    }

    private static NormalizedEvent? NormalizeSessionStarted(JsonElement properties)
    {
        var info = ObjectProperty(properties, "info") ?? properties;
        var sessionId = OptionalString(info, "id") ?? SessionId(properties);
        var model = OptionalString(info, "model");
        var cwd = OptionalString(info, "directory") ?? OptionalString(info, "worktree");
        return Event(
            RuntimeEventTypes.SessionStarted,
            sessionId,
            OptionalObject(("model", model)),
            cwd: cwd);
    }

    private static NormalizedEvent? NormalizeStatus(JsonElement properties)
    {
        var status = OptionalString(properties, "status");
        if (status is null && ObjectProperty(properties, "status") is { } statusObject)
        {
            status = OptionalString(statusObject, "type");
        }
        return status switch
        {
            "running" or "busy" or "retry" => Event(
                RuntimeEventTypes.TurnStarted,
                SessionId(properties),
                OptionalObject()),
            "idle" => Event(
                RuntimeEventTypes.TurnCompleted,
                SessionId(properties),
                OptionalObject()),
            _ => null,
        };
    }

    private static NormalizedEvent? NormalizePermissionRequested(
        JsonElement properties,
        DateTimeOffset occurredAt,
        TimeSpan approvalLifetime)
    {
        var requestId = OptionalString(properties, "id");
        var sessionId = SessionId(properties);
        var title = OptionalString(properties, "action") ??
            OptionalString(properties, "type") ??
            "OpenCode 权限请求";
        if (requestId is null)
        {
            return null;
        }
        return Event(
            RuntimeEventTypes.ApprovalRequested,
            sessionId,
            OptionalObject(
                ("requestId", requestId),
                ("title", title),
                ("description", OptionalString(properties, "description")),
                ("expiresAt", (occurredAt + approvalLifetime).ToString("O"))),
            requestId);
    }

    private static NormalizedEvent? NormalizePermissionResolved(JsonElement properties)
    {
        var requestId = OptionalString(properties, "requestID") ??
            OptionalString(properties, "requestId") ??
            OptionalString(properties, "id");
        var reply = OptionalString(properties, "reply");
        if (requestId is null || reply is null)
        {
            return null;
        }
        var outcome = reply == "reject" ? "denied" : "allowed";
        return Event(
            RuntimeEventTypes.ApprovalResolvedExternally,
            SessionId(properties),
            OptionalObject(("requestId", requestId), ("outcome", outcome)),
            requestId);
    }

    private static NormalizedEvent? NormalizeQuestionAsked(
        JsonElement properties,
        DateTimeOffset occurredAt,
        TimeSpan inputLifetime)
    {
        var requestId = OptionalString(properties, "id");
        if (requestId is null ||
            !properties.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var normalizedQuestions = new List<object>();
        var index = 0;
        foreach (var question in questions.EnumerateArray())
        {
            index++;
            var prompt = OptionalString(question, "question");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }
            var options = question.TryGetProperty("options", out var rawOptions) &&
                rawOptions.ValueKind == JsonValueKind.Array
                    ? rawOptions.EnumerateArray()
                        .Select(option => OptionalString(option, "label"))
                        .Where(option => !string.IsNullOrWhiteSpace(option))
                        .Cast<string>()
                        .ToArray()
                    : [];
            normalizedQuestions.Add(new
            {
                id = $"opencode_question_{index}",
                header = OptionalString(question, "header") ?? $"问题 {index}",
                prompt,
                options,
                multiple = OptionalBoolean(question, "multiple"),
                allowsCustom = OptionalBooleanValue(question, "custom") ?? true,
                isSecret = false,
            });
        }
        if (normalizedQuestions.Count == 0)
        {
            return null;
        }
        return Event(
            RuntimeEventTypes.InputRequested,
            SessionId(properties),
            JsonSerializer.SerializeToElement(new
            {
                requestId,
                questions = normalizedQuestions,
                expiresAt = (occurredAt + inputLifetime).ToString("O"),
            }),
            requestId);
    }

    private static NormalizedEvent? NormalizeQuestionResolved(JsonElement properties)
    {
        var requestId = OptionalString(properties, "requestID") ??
            OptionalString(properties, "requestId") ??
            OptionalString(properties, "id");
        return requestId is null
            ? null
            : Event(
                RuntimeEventTypes.InputResolvedExternally,
                SessionId(properties),
                OptionalObject(("requestId", requestId)),
                requestId);
    }

    private static NormalizedEvent? NormalizeMessagePart(JsonElement properties)
    {
        var part = ObjectProperty(properties, "part");
        var state = part is { } partValue ? ObjectProperty(partValue, "state") : null;
        if (part is null || OptionalString(part.Value, "type") != "tool" || state is null)
        {
            return null;
        }
        var status = OptionalString(state.Value, "status");
        var tool = OptionalString(part.Value, "tool") ?? "tool";
        var summary = status switch
        {
            "pending" or "running" => $"正在执行 {tool}",
            "completed" => $"已完成 {tool}",
            "error" => $"{tool} 执行失败",
            _ => null,
        };
        return summary is null
            ? null
            : Event(
                RuntimeEventTypes.TurnActivity,
                SessionId(properties) ?? OptionalString(part.Value, "sessionID"),
                OptionalObject(("summary", summary)));
    }

    private static JsonElement UnwrapData(JsonElement properties)
    {
        return ObjectProperty(properties, "data") ?? properties;
    }

    private static string? DeduplicationFingerprint(
        OpenCodeRawEvent rawEvent,
        NormalizedEvent normalized)
    {
        return rawEvent.Type switch
        {
            "permission.asked" or "permission.v2.asked" or "permission.updated" or
            "permission.replied" or "permission.v2.replied" or
            "question.asked" or "question.replied" or "question.rejected" =>
                normalized.CorrelationId is null
                    ? null
                    : $"{rawEvent.Type}:{normalized.SessionId}:{normalized.CorrelationId}:{rawEvent.Properties.GetRawText()}",
            "message.part.updated" => MessagePartFingerprint(rawEvent, normalized),
            _ => null,
        };
    }

    private static string? MessagePartFingerprint(
        OpenCodeRawEvent rawEvent,
        NormalizedEvent normalized)
    {
        var properties = UnwrapData(rawEvent.Properties);
        var part = ObjectProperty(properties, "part");
        var partId = part is { } partValue ? OptionalString(partValue, "id") : null;
        return partId is null
            ? null
            : $"{rawEvent.Type}:{normalized.SessionId}:{partId}:{rawEvent.Properties.GetRawText()}";
    }

    private static string? SessionId(JsonElement properties)
    {
        return OptionalString(properties, "sessionID") ??
            OptionalString(properties, "sessionId") ??
            OptionalString(properties, "session_id") ??
            (ObjectProperty(properties, "info") is { } info ? OptionalString(info, "id") : null);
    }

    private static string? ErrorText(JsonElement properties)
    {
        if (!properties.TryGetProperty("error", out var error))
        {
            return null;
        }
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }
        if (error.ValueKind == JsonValueKind.Object)
        {
            return OptionalString(error, "message") ??
                (ObjectProperty(error, "data") is { } data ? OptionalString(data, "message") : null);
        }
        return null;
    }

    private static NormalizedEvent Event(
        string eventType,
        string? sessionId,
        JsonElement payload,
        string? correlationId = null,
        string? cwd = null) =>
        new(eventType, sessionId, cwd, payload, correlationId);

    private static JsonElement OptionalObject(params (string Name, object? Value)[] values)
    {
        return JsonSerializer.SerializeToElement(values
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal));
    }

    private static JsonElement? ObjectProperty(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Object
                ? property
                : null;
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

    private readonly record struct NormalizedEvent(
        string EventType,
        string? SessionId,
        string? Cwd,
        JsonElement Payload,
        string? CorrelationId);
}
