using System.Text.Json;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public sealed class FeishuEventNormalizer(IFeishuInboundDeduplicator deduplicator)
{
    public void Release(string eventId) => deduplicator.Release(eventId);

    public FeishuNormalizationResult NormalizeMessage(
        string eventId,
        string traceId,
        JsonElement payload)
    {
        if (!TryStart(eventId, traceId, out var failure))
        {
            return failure!;
        }
        var senderId = Text(payload, "sender", "sender_id", "open_id");
        if (!TryObject(payload, "message", out var message))
        {
            return FeishuNormalizationResult.Rejected("飞书消息缺少 message。 ");
        }
        var messageId = Text(message, "message_id");
        var chatId = Text(message, "chat_id");
        if (senderId is null || messageId is null || chatId is null)
        {
            return FeishuNormalizationResult.Rejected("飞书消息缺少发送者、消息或会话 ID。 ");
        }
        var messageType = Text(message, "message_type") ?? "text";
        var content = ParseEmbeddedObject(Text(message, "content"));
        var text = ExtractText(messageType, content).Trim();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["messageType"] = messageType,
        };
        var parentId = Text(message, "parent_id");
        if (parentId is not null)
        {
            parameters["parentMessageId"] = parentId;
        }
        return FeishuNormalizationResult.Accepted(new(
            eventId,
            MessageIntentType(text),
            senderId,
            chatId,
            messageId,
            Text(message, "chat_type") ?? "unknown",
            traceId,
            text,
            parameters,
            ExtractAttachments(messageType, content)));
    }

    public FeishuNormalizationResult NormalizeCardAction(
        string eventId,
        string traceId,
        JsonElement payload)
    {
        if (!TryStart(eventId, traceId, out var failure))
        {
            return failure!;
        }
        var openId = Text(payload, "operator", "open_id");
        var messageId = Text(payload, "context", "open_message_id") ??
            Text(payload, "open_message_id");
        var chatId = Text(payload, "context", "open_chat_id") ??
            Text(payload, "open_chat_id");
        if (openId is null || messageId is null || chatId is null)
        {
            return FeishuNormalizationResult.Rejected("飞书卡片回调缺少操作者、消息或会话 ID。 ");
        }
        if (!TryObject(payload, "action", out var actionNode) ||
            !TryActionValue(actionNode, out var value))
        {
            return FeishuNormalizationResult.Rejected("飞书卡片操作参数不完整。 ");
        }
        var action = Text(value, "action");
        if (action is null || !FeishuCardActions.All.Contains(action))
        {
            return FeishuNormalizationResult.Rejected("无法识别这个飞书卡片操作。 ");
        }
        var parameters = StringProperties(value);
        if (TryObject(actionNode, "form_value", out var form))
        {
            foreach (var property in StringProperties(form))
            {
                parameters[$"form.{property.Key}"] = property.Value;
            }
        }
        var validationError = ValidateAction(action, parameters);
        if (validationError is not null)
        {
            return FeishuNormalizationResult.Rejected(validationError);
        }
        return FeishuNormalizationResult.Accepted(new(
            eventId,
            IntentType(action),
            openId,
            chatId,
            messageId,
            "card",
            traceId,
            Parameters: parameters));
    }

    private bool TryStart(
        string eventId,
        string traceId,
        out FeishuNormalizationResult? failure)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(traceId))
        {
            failure = FeishuNormalizationResult.Rejected("飞书事件缺少 eventId 或 traceId。 ");
            return false;
        }
        if (!deduplicator.TryClaim(eventId))
        {
            failure = FeishuNormalizationResult.AlreadyProcessed();
            return false;
        }
        failure = null;
        return true;
    }

    private static string MessageIntentType(string text)
    {
        var command = text.Trim();
        if (command == "/") return FeishuIntentTypes.CommandMenu;
        var first = command.Split([' ', '\t', '\r', '\n'], 2)[0];
        return first switch
        {
            "/new" or "/新建" or "新建" => FeishuIntentTypes.CommandNew,
            "/workspace" or "/工作区" => FeishuIntentTypes.CommandWorkspace,
            "/status" or "/状态" => FeishuIntentTypes.CommandStatus,
            "/sessions" or "/会话" or "/会话管理" => FeishuIntentTypes.CommandSessions,
            "/aliases" or "/别名" or "/会话别名" or "别名" =>
                FeishuIntentTypes.CommandAliases,
            "/help" or "/帮助" => FeishuIntentTypes.CommandHelp,
            _ => FeishuIntentTypes.MessagePrompt,
        };
    }

    private static string IntentType(string action) => action switch
    {
        FeishuCardActions.CommandNew => FeishuIntentTypes.CommandNew,
        FeishuCardActions.CommandSessions => FeishuIntentTypes.CommandSessions,
        FeishuCardActions.CommandStatus => FeishuIntentTypes.CommandStatus,
        FeishuCardActions.CommandWorkspace => FeishuIntentTypes.CommandWorkspace,
        FeishuCardActions.CommandAliases => FeishuIntentTypes.CommandAliases,
        FeishuCardActions.CommandHelp => FeishuIntentTypes.CommandHelp,
        FeishuCardActions.RuntimeNewSelect => FeishuIntentTypes.RuntimeNewSelect,
        FeishuCardActions.RuntimeNewSubmit => FeishuIntentTypes.RuntimeNewSubmit,
        FeishuCardActions.RuntimeNewCancel => FeishuIntentTypes.RuntimeNewCancel,
        FeishuCardActions.RetryStop => FeishuIntentTypes.RetryStop,
        FeishuCardActions.ApprovalAllow or FeishuCardActions.ApprovalDeny =>
            FeishuIntentTypes.ApprovalResolve,
        FeishuCardActions.ApprovalDesktop => FeishuIntentTypes.ApprovalDeferToLocal,
        FeishuCardActions.InputAnswer => FeishuIntentTypes.InputAnswer,
        FeishuCardActions.InputToggle => FeishuIntentTypes.InputToggle,
        FeishuCardActions.InputSubmit => FeishuIntentTypes.InputSubmit,
        FeishuCardActions.InputLocal => FeishuIntentTypes.InputDeferToLocal,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static string? ValidateAction(
        string action,
        Dictionary<string, string> parameters)
    {
        var required = action switch
        {
            FeishuCardActions.RuntimeNewSelect or
            FeishuCardActions.RuntimeNewSubmit or
            FeishuCardActions.RuntimeNewCancel => new[] { "flowId", "runtime" },
            FeishuCardActions.RetryStop => new[] { "sessionId", "retryCycleId" },
            FeishuCardActions.ApprovalAllow or
            FeishuCardActions.ApprovalDeny or
            FeishuCardActions.ApprovalDesktop or
            FeishuCardActions.InputLocal => new[] { "requestId", "sessionId" },
            FeishuCardActions.InputAnswer or
            FeishuCardActions.InputToggle =>
                new[] { "requestId", "sessionId", "questionId", "answer" },
            FeishuCardActions.InputSubmit =>
                new[] { "requestId", "sessionId", "questionId" },
            _ => [],
        };
        return required.FirstOrDefault(field =>
            !parameters.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) is { } missing
            ? $"飞书卡片操作缺少 {missing}。 "
            : null;
    }

    private static Dictionary<string, string> StringProperties(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { } value &&
                !string.IsNullOrWhiteSpace(value))
            {
                result[property.Name] = value;
            }
        }
        var action = result.GetValueOrDefault("action");
        if (action == FeishuCardActions.ApprovalAllow)
        {
            result["resolution"] = "allow";
        }
        else if (action == FeishuCardActions.ApprovalDeny)
        {
            result["resolution"] = "deny";
        }
        return result;
    }

    private static bool TryActionValue(JsonElement action, out JsonElement value)
    {
        value = default;
        if (!action.TryGetProperty("value", out var raw))
        {
            return false;
        }
        if (raw.ValueKind == JsonValueKind.Object)
        {
            value = raw;
            return true;
        }
        if (raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(raw.GetString() ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<FeishuAttachment> ExtractAttachments(
        string messageType,
        JsonElement content)
    {
        var result = new List<FeishuAttachment>();
        AddAttachment(result, content, "image_key", "image");
        AddAttachment(result, content, "file_key", messageType == "image" ? "image" : "file");
        return result;
    }

    private static void AddAttachment(
        List<FeishuAttachment> result,
        JsonElement content,
        string keyName,
        string kind)
    {
        var key = Text(content, keyName);
        if (key is not null)
        {
            result.Add(new(kind, key, Text(content, "file_name")));
        }
    }

    private static string ExtractText(string messageType, JsonElement content)
    {
        if (messageType == "text")
        {
            return Text(content, "text") ?? "";
        }
        return Text(content, "text") ?? Text(content, "title") ?? "";
    }

    private static JsonElement ParseEmbeddedObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject();
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : EmptyObject();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static bool TryObject(
        JsonElement element,
        string property,
        out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        result = default;
        return false;
    }

    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(part, out element))
            {
                return null;
            }
        }
        return element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()
            : null;
    }
}
