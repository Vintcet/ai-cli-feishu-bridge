using System.Text.Json;

namespace AiCliFeishu.Bridge.Protocol;

public sealed record BridgeProtocolValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

public static class BridgeProtocolValidator
{
    private static readonly IReadOnlySet<string> PromptModes = Set("steer", "queue");
    private static readonly IReadOnlySet<string> ApprovalDecisions =
        Set("allow_once", "allow_session", "deny");
    private static readonly IReadOnlySet<string> ApprovalOutcomes =
        Set("allowed", "denied", "cancelled");

    public static BridgeProtocolValidationResult Validate(RuntimeCommandEnvelope command)
    {
        var errors = ValidateEnvelope(command);
        RequiredText(command.CommandId, "commandId", errors);
        RequiredText(command.CommandType, "commandType", errors);
        Timestamp(command.CreatedAt, "createdAt", errors);
        Object(command.Payload, "payload", errors);

        if (!RuntimeCommandTypes.All.Contains(command.CommandType))
        {
            errors.Add($"不支持的 commandType：{command.CommandType}");
        }
        else if (command.Payload.ValueKind == JsonValueKind.Object)
        {
            ValidateCommandPayload(command, errors);
        }
        return Result(errors);
    }

    public static BridgeProtocolValidationResult Validate(RuntimeEventEnvelope runtimeEvent)
    {
        var errors = ValidateEnvelope(runtimeEvent);
        RequiredText(runtimeEvent.EventId, "eventId", errors);
        RequiredText(runtimeEvent.EventType, "eventType", errors);
        Timestamp(runtimeEvent.OccurredAt, "occurredAt", errors);
        Object(runtimeEvent.Payload, "payload", errors);

        if (!RuntimeEventTypes.All.Contains(runtimeEvent.EventType))
        {
            errors.Add($"不支持的 eventType：{runtimeEvent.EventType}");
        }
        else if (runtimeEvent.Payload.ValueKind == JsonValueKind.Object)
        {
            ValidateEventPayload(runtimeEvent, errors);
        }
        return Result(errors);
    }

    private static List<string> ValidateEnvelope(RuntimeEnvelope envelope)
    {
        var errors = new List<string>();
        if (envelope.ProtocolVersion != BridgeProtocolVersion.Current)
        {
            errors.Add(
                $"protocolVersion 必须是 {BridgeProtocolVersion.Current}，实际是 {envelope.ProtocolVersion}。");
        }
        if (!RuntimeNames.All.Contains(envelope.Runtime))
        {
            errors.Add($"不支持的 runtime：{envelope.Runtime}");
        }
        if (envelope.Session is null)
        {
            errors.Add("session 必须是对象。");
        }
        else
        {
            RequiredText(envelope.Session.ExternalId, "session.externalId", errors);
        }
        RequiredText(envelope.TraceId, "traceId", errors);
        if (envelope.CorrelationId is not null)
        {
            RequiredText(envelope.CorrelationId, "correlationId", errors);
        }
        return errors;
    }

    private static void ValidateCommandPayload(
        RuntimeCommandEnvelope command,
        List<string> errors)
    {
        var payload = command.Payload;
        switch (command.CommandType)
        {
            case RuntimeCommandTypes.PromptSend:
                RequiredString(payload, "prompt", errors);
                RequiredEnum(payload, "mode", PromptModes, errors);
                break;
            case RuntimeCommandTypes.ApprovalResolve:
                RequiredString(payload, "requestId", errors);
                RequiredEnum(payload, "decision", ApprovalDecisions, errors);
                break;
            case RuntimeCommandTypes.InputResolve:
                RequiredString(payload, "requestId", errors);
                ValidateAnswers(payload, errors);
                break;
            case RuntimeCommandTypes.SessionLaunch:
                RequiredString(payload, "cwd", errors);
                OptionalKind(payload, "prompt", JsonValueKind.String, errors);
                OptionalKind(
                    payload,
                    "elevated",
                    JsonValueKind.True,
                    errors,
                    JsonValueKind.False);
                break;
            case RuntimeCommandTypes.SessionResume:
                OptionalKind(payload, "prompt", JsonValueKind.String, errors);
                break;
            case RuntimeCommandTypes.SessionStop:
                OptionalKind(payload, "reason", JsonValueKind.String, errors);
                break;
        }
    }

    private static void ValidateAnswers(JsonElement payload, List<string> errors)
    {
        if (!TryProperty(payload, "answers", JsonValueKind.Object, errors, out var answers))
        {
            return;
        }
        foreach (var answer in answers.EnumerateObject())
        {
            if (answer.Value.ValueKind == JsonValueKind.String)
            {
                continue;
            }
            if (
                answer.Value.ValueKind != JsonValueKind.Array ||
                answer.Value.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            {
                errors.Add($"payload.answers.{answer.Name} 必须是字符串或字符串数组。");
            }
        }
    }

    private static void ValidateEventPayload(
        RuntimeEventEnvelope runtimeEvent,
        List<string> errors)
    {
        var payload = runtimeEvent.Payload;
        switch (runtimeEvent.EventType)
        {
            case RuntimeEventTypes.SessionStarted:
                OptionalKind(payload, "model", JsonValueKind.String, errors);
                break;
            case RuntimeEventTypes.SessionEnded:
            case RuntimeEventTypes.RuntimeDisconnected:
                OptionalKind(payload, "reason", JsonValueKind.String, errors);
                break;
            case RuntimeEventTypes.TurnStarted:
                OptionalKind(payload, "turnId", JsonValueKind.String, errors);
                break;
            case RuntimeEventTypes.TurnActivity:
                OptionalKind(payload, "turnId", JsonValueKind.String, errors);
                RequiredString(payload, "summary", errors);
                break;
            case RuntimeEventTypes.TurnCompleted:
                OptionalKind(payload, "turnId", JsonValueKind.String, errors);
                OptionalKind(payload, "message", JsonValueKind.String, errors);
                break;
            case RuntimeEventTypes.TurnFailed:
                OptionalKind(payload, "turnId", JsonValueKind.String, errors);
                RequiredString(payload, "error", errors);
                OptionalKind(payload, "code", JsonValueKind.String, errors);
                break;
            case RuntimeEventTypes.ApprovalRequested:
                RequiredString(payload, "requestId", errors);
                RequiredString(payload, "title", errors);
                OptionalKind(payload, "description", JsonValueKind.String, errors);
                RequiredTimestamp(payload, "expiresAt", errors);
                break;
            case RuntimeEventTypes.ApprovalResolvedExternally:
                RequiredString(payload, "requestId", errors);
                RequiredEnum(payload, "outcome", ApprovalOutcomes, errors);
                break;
            case RuntimeEventTypes.InputRequested:
                RequiredString(payload, "requestId", errors);
                ValidateQuestions(payload, errors);
                RequiredTimestamp(payload, "expiresAt", errors);
                break;
            case RuntimeEventTypes.InputResolvedExternally:
                RequiredString(payload, "requestId", errors);
                break;
            case RuntimeEventTypes.RuntimeConnected:
                OptionalKind(payload, "endpoint", JsonValueKind.String, errors);
                break;
        }
    }

    private static void ValidateQuestions(JsonElement payload, List<string> errors)
    {
        if (!TryProperty(payload, "questions", JsonValueKind.Array, errors, out var questions))
        {
            return;
        }
        var index = 0;
        foreach (var question in questions.EnumerateArray())
        {
            var prefix = $"payload.questions[{index}]";
            if (question.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{prefix} 必须是对象。");
                index++;
                continue;
            }
            RequiredString(question, "id", errors, $"{prefix}.id");
            RequiredString(question, "prompt", errors, $"{prefix}.prompt");
            OptionalStringArray(question, "options", errors, $"{prefix}.options");
            OptionalKind(
                question,
                "multiple",
                JsonValueKind.True,
                errors,
                JsonValueKind.False,
                $"{prefix}.multiple");
            OptionalKind(
                question,
                "allowsCustom",
                JsonValueKind.True,
                errors,
                JsonValueKind.False,
                $"{prefix}.allowsCustom");
            index++;
        }
    }

    private static void RequiredTimestamp(
        JsonElement owner,
        string name,
        List<string> errors)
    {
        if (!owner.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), out _))
        {
            errors.Add($"payload.{name} 必须是有效时间戳。");
        }
    }

    private static void RequiredString(
        JsonElement owner,
        string name,
        List<string> errors,
        string? path = null)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path ?? $"payload.{name}"} 必须是非空字符串。");
            return;
        }
        RequiredText(value.GetString(), path ?? $"payload.{name}", errors);
    }

    private static void RequiredEnum(
        JsonElement owner,
        string name,
        IReadOnlySet<string> allowed,
        List<string> errors)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"payload.{name} 必须是字符串。");
            return;
        }
        var text = value.GetString() ?? string.Empty;
        if (!allowed.Contains(text))
        {
            errors.Add($"payload.{name} 的值 {text} 不受支持。");
        }
    }

    private static bool TryProperty(
        JsonElement owner,
        string name,
        JsonValueKind expected,
        List<string> errors,
        out JsonElement value)
    {
        if (!owner.TryGetProperty(name, out value) || value.ValueKind != expected)
        {
            errors.Add($"payload.{name} 必须是 {expected}。");
            return false;
        }
        return true;
    }

    private static void OptionalKind(
        JsonElement owner,
        string name,
        JsonValueKind expected,
        List<string> errors,
        JsonValueKind? alternative = null,
        string? path = null)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return;
        }
        if (value.ValueKind != expected && value.ValueKind != alternative)
        {
            errors.Add($"{path ?? $"payload.{name}"} 的类型不正确。");
        }
    }

    private static void OptionalStringArray(
        JsonElement owner,
        string name,
        List<string> errors,
        string path)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return;
        }
        if (
            value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            errors.Add($"{path} 必须是字符串数组。");
        }
    }

    private static void RequiredText(string? value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} 必须是非空字符串。");
        }
    }

    private static void Timestamp(string value, string path, List<string> errors)
    {
        if (!DateTimeOffset.TryParse(value, out _))
        {
            errors.Add($"{path} 必须是有效时间戳。");
        }
    }

    private static void Object(JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} 必须是对象。");
        }
    }

    private static BridgeProtocolValidationResult Result(List<string> errors)
    {
        return new(errors.Count == 0, errors);
    }

    private static IReadOnlySet<string> Set(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}
