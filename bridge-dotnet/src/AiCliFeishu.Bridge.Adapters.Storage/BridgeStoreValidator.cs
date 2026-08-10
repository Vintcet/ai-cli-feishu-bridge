using System.Globalization;
using System.Text.Json;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public sealed class BridgeStoreValidationException(
    string fileName,
    IReadOnlyList<string> errors)
    : IOException($"{fileName} 与 Bridge Store 结构不兼容：{string.Join("；", errors)}")
{
    public string FileName { get; } = fileName;
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class BridgeStoreValidator
{
    private static readonly IReadOnlySet<string> ApprovalStatusesAll =
        new HashSet<string>(
            [ApprovalStatuses.Pending, ApprovalStatuses.Resolved, ApprovalStatuses.Orphaned],
            StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PendingInputStatuses =
        new HashSet<string>([InputRequestStatuses.Pending], StringComparer.Ordinal);

    public static void Validate(BridgeStoreFile file, JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("根节点必须是对象");
            throw new BridgeStoreValidationException(file.FileName, errors);
        }

        switch (file.Kind)
        {
            case BridgeStoreFileKind.Bindings:
                ValidateObjectMap(root, "users", errors, ValidateBinding, "openId");
                OptionalString(root, "ownerOpenId", errors);
                OptionalString(root, "pairingCode", errors);
                break;
            case BridgeStoreFileKind.Sessions:
                ValidateObjectMap(root, "sessions", errors, ValidateSession, "sessionId");
                ValidatePendingInputs(root, errors);
                break;
            case BridgeStoreFileKind.Routes:
                ValidateOptionalObjectMap(
                    root,
                    "messages",
                    errors,
                    ValidateRoute,
                    "messageId");
                ValidateOptionalTimestampMap(root, "processedInbound", errors);
                break;
            case BridgeStoreFileKind.Approvals:
                ValidateObjectMap(
                    root,
                    "requests",
                    errors,
                    ValidateApproval,
                    "requestId");
                break;
            case BridgeStoreFileKind.Settings:
            case BridgeStoreFileKind.ControlToken:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(file));
        }

        if (errors.Count > 0)
        {
            throw new BridgeStoreValidationException(file.FileName, errors);
        }
    }

    public static void ValidateSnapshot(BridgeStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateDocument(BridgeStoreFile.Bindings, snapshot.Bindings);
        ValidateDocument(BridgeStoreFile.Sessions, snapshot.Sessions);
        ValidateDocument(BridgeStoreFile.Routes, snapshot.Routes);
        ValidateDocument(BridgeStoreFile.Approvals, snapshot.Approvals);
        ValidateDocument(BridgeStoreFile.Settings, snapshot.Settings);
        ValidateDocument(BridgeStoreFile.ControlToken, snapshot.ControlToken);

        var approvalErrors = snapshot.Approvals.Requests
            .Where(item => !snapshot.Sessions.Sessions.ContainsKey(item.Value.SessionId))
            .Select(item =>
                $"requests.{item.Key}.sessionId 引用了不存在的会话 {item.Value.SessionId}")
            .ToArray();
        if (approvalErrors.Length > 0)
        {
            throw new BridgeStoreValidationException(
                BridgeStoreFile.Approvals.FileName,
                approvalErrors);
        }

        var inputErrors = BridgeStoreCoreProjection.ProjectInputs(snapshot).Requests
            .Where(item => !snapshot.Sessions.Sessions.ContainsKey(item.Value.SessionId))
            .Select(item =>
                $"{BridgeStoreInputPersistence.ExtensionPropertyName}.{item.Key}.sessionId " +
                $"引用了不存在的会话 {item.Value.SessionId}")
            .ToArray();
        if (inputErrors.Length > 0)
        {
            throw new BridgeStoreValidationException(
                BridgeStoreFile.Sessions.FileName,
                inputErrors);
        }

        var routeErrors = snapshot.Routes.Messages
            .Where(item => !snapshot.Sessions.Sessions.ContainsKey(item.Value.SessionId))
            .Select(item =>
                $"messages.{item.Key}.sessionId 引用了不存在的会话 {item.Value.SessionId}")
            .ToArray();
        if (routeErrors.Length > 0)
        {
            throw new BridgeStoreValidationException(
                BridgeStoreFile.Routes.FileName,
                routeErrors);
        }
    }

    private static void ValidateBinding(JsonElement value, string path, List<string> errors)
    {
        RequiredNonBlankString(value, "openId", path, errors);
        RequiredNonBlankString(value, "chatId", path, errors);
        RequiredString(value, "chatType", path, errors);
        RequiredTimestamp(value, "boundAt", path, errors);
    }

    private static void ValidateSession(JsonElement value, string path, List<string> errors)
    {
        RequiredNonBlankString(value, "sessionId", path, errors);
        RequiredNonBlankString(value, "cwd", path, errors);
        KnownStatus(value, "status", path, SessionStatuses.All, errors);
        OptionalTimestamp(value, "openedAt", path, errors);
        RequiredTimestamp(value, "lastSeenAt", path, errors);
        OptionalTimestamp(value, "endedAt", path, errors);
    }

    private static void ValidateRoute(JsonElement value, string path, List<string> errors)
    {
        RequiredNonBlankString(value, "messageId", path, errors);
        RequiredNonBlankString(value, "sessionId", path, errors);
        RequiredNonBlankString(value, "chatId", path, errors);
        RequiredString(value, "kind", path, errors);
        RequiredTimestamp(value, "createdAt", path, errors);
    }

    private static void ValidateApproval(JsonElement value, string path, List<string> errors)
    {
        RequiredNonBlankString(value, "requestId", path, errors);
        RequiredNonBlankString(value, "sessionId", path, errors);
        RequiredString(value, "turnId", path, errors);
        RequiredString(value, "cwd", path, errors);
        RequiredString(value, "toolName", path, errors);
        RequiredString(value, "toolPreview", path, errors);
        RequiredTimestamp(value, "createdAt", path, errors);
        RequiredTimestamp(value, "expiresAt", path, errors);
        KnownStatus(value, "status", path, ApprovalStatusesAll, errors);
        OptionalTimestamp(value, "resolvedAt", path, errors);
        if (!value.TryGetProperty("messageIds", out var messageIds) ||
            messageIds.ValueKind != JsonValueKind.Array ||
            messageIds.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            errors.Add($"{path}.messageIds 必须是字符串数组");
        }
    }

    private static void ValidatePendingInputs(JsonElement root, List<string> errors)
    {
        var matches = root.EnumerateObject()
            .Where(item => string.Equals(
                item.Name,
                BridgeStoreInputPersistence.ExtensionPropertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return;
        }
        if (matches.Length != 1)
        {
            errors.Add(
                $"{BridgeStoreInputPersistence.ExtensionPropertyName} 扩展字段不能重复");
            return;
        }
        if (matches[0].Value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(
                $"{BridgeStoreInputPersistence.ExtensionPropertyName} 必须是对象");
            return;
        }
        ValidateMapItems(
            matches[0].Value,
            BridgeStoreInputPersistence.ExtensionPropertyName,
            errors,
            ValidatePendingInput,
            "requestId");
    }

    private static void ValidatePendingInput(
        JsonElement value,
        string path,
        List<string> errors)
    {
        RequiredNonBlankString(value, "requestId", path, errors);
        RequiredNonBlankString(value, "sessionId", path, errors);
        KnownStatus(value, "status", path, PendingInputStatuses, errors);
        RequiredTimestamp(value, "createdAt", path, errors);
        RequiredTimestamp(value, "expiresAt", path, errors);
        if (value.TryGetProperty("createdAt", out var createdValue) &&
            value.TryGetProperty("expiresAt", out var expiresValue) &&
            TryTimestamp(createdValue, out var createdAt) &&
            TryTimestamp(expiresValue, out var expiresAt) &&
            expiresAt <= createdAt)
        {
            errors.Add($"{path}.expiresAt 必须晚于 createdAt");
        }

        var questions = new Dictionary<string, PendingQuestionShape>(StringComparer.Ordinal);
        if (!value.TryGetProperty("questions", out var questionValues) ||
            questionValues.ValueKind != JsonValueKind.Array ||
            questionValues.GetArrayLength() == 0)
        {
            errors.Add($"{path}.questions 必须是非空数组");
        }
        else
        {
            var index = 0;
            foreach (var question in questionValues.EnumerateArray())
            {
                var questionPath = $"{path}.questions[{index++}]";
                if (question.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{questionPath} 必须是对象");
                    continue;
                }
                RequiredNonBlankString(question, "id", questionPath, errors);
                RequiredBoolean(question, "multiple", questionPath, errors);
                RequiredBoolean(question, "allowsCustom", questionPath, errors);
                RequiredBoolean(question, "isSecret", questionPath, errors);
                OptionalString(question, "header", questionPath, errors);
                OptionalString(question, "prompt", questionPath, errors);

                var options = StringArray(question, "options", questionPath, errors);
                if (!TryString(question, "id", out var questionId) ||
                    string.IsNullOrWhiteSpace(questionId) ||
                    !TryBoolean(question, "multiple", out var multiple) ||
                    !TryBoolean(question, "allowsCustom", out var allowsCustom) ||
                    !TryBoolean(question, "isSecret", out var isSecret))
                {
                    continue;
                }
                if (!questions.TryAdd(
                        questionId,
                        new PendingQuestionShape(
                            multiple,
                            allowsCustom,
                            isSecret,
                            options.ToHashSet(StringComparer.Ordinal))))
                {
                    errors.Add($"{questionPath}.id 不能重复");
                }
            }
        }

        if (!value.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.answers 必须是对象");
            return;
        }
        foreach (var answer in answers.EnumerateObject())
        {
            var answerPath = $"{path}.answers.{answer.Name}";
            if (!questions.TryGetValue(answer.Name, out var question))
            {
                errors.Add($"{answerPath} 未对应已登记的问题");
                continue;
            }
            if (question.IsSecret)
            {
                errors.Add($"{answerPath} 是敏感答案，不允许落盘");
            }
            if (answer.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{answerPath} 必须是字符串数组");
                continue;
            }
            var values = answer.Value.EnumerateArray().ToArray();
            if (values.Length == 0 ||
                values.Any(item => item.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(item.GetString())))
            {
                errors.Add($"{answerPath} 必须是非空字符串数组");
                continue;
            }
            if (!question.Multiple && values.Length != 1)
            {
                errors.Add($"{answerPath} 只能包含一个答案");
            }
            if (!question.AllowsCustom && values.Any(item =>
                    !question.Options.Contains(item.GetString()!)))
            {
                errors.Add($"{answerPath} 包含问题选项之外的答案");
            }
        }
    }

    private static void ValidateObjectMap(
        JsonElement root,
        string property,
        List<string> errors,
        Action<JsonElement, string, List<string>> validateItem,
        string? identityProperty = null)
    {
        if (!root.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{property} 必须是对象");
            return;
        }
        ValidateMapItems(map, property, errors, validateItem, identityProperty);
    }

    private static void ValidateOptionalObjectMap(
        JsonElement root,
        string property,
        List<string> errors,
        Action<JsonElement, string, List<string>> validateItem,
        string? identityProperty = null)
    {
        if (!root.TryGetProperty(property, out var map))
        {
            return;
        }
        if (map.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{property} 必须是对象");
            return;
        }
        ValidateMapItems(map, property, errors, validateItem, identityProperty);
    }

    private static void ValidateMapItems(
        JsonElement map,
        string property,
        List<string> errors,
        Action<JsonElement, string, List<string>> validateItem,
        string? identityProperty)
    {
        foreach (var item in map.EnumerateObject())
        {
            var path = $"{property}.{item.Name}";
            if (item.Value.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} 必须是对象");
                continue;
            }
            validateItem(item.Value, path, errors);
            if (identityProperty is not null &&
                item.Value.TryGetProperty(identityProperty, out var identity) &&
                identity.ValueKind == JsonValueKind.String &&
                !string.Equals(item.Name, identity.GetString(), StringComparison.Ordinal))
            {
                errors.Add(
                    $"{path}.{identityProperty} 必须与 map key {item.Name} 一致");
            }
        }
    }

    private static void ValidateOptionalTimestampMap(
        JsonElement root,
        string property,
        List<string> errors)
    {
        if (!root.TryGetProperty(property, out var map))
        {
            return;
        }
        if (map.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{property} 必须是对象");
            return;
        }
        foreach (var item in map.EnumerateObject())
        {
            if (!TryTimestamp(item.Value, out _))
            {
                errors.Add($"{property}.{item.Name} 必须是有效时间戳");
            }
        }
    }

    private static void ValidateDocument<T>(BridgeStoreFile file, T value)
        where T : class
    {
        var element = JsonSerializer.SerializeToElement(value, BridgeStoreJson.Options);
        Validate(file, element);
    }

    private static void KnownStatus(
        JsonElement owner,
        string property,
        string path,
        IReadOnlySet<string> known,
        List<string> errors)
    {
        if (!TryString(owner, property, out var value))
        {
            errors.Add($"{path}.{property} 必须是字符串");
            return;
        }
        if (!known.Contains(value))
        {
            errors.Add($"{path}.{property} 包含未知状态 {value}");
        }
    }

    private static void RequiredTimestamp(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (!owner.TryGetProperty(property, out var value) || !TryTimestamp(value, out _))
        {
            errors.Add($"{path}.{property} 必须是有效时间戳");
        }
    }

    private static void OptionalTimestamp(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (owner.TryGetProperty(property, out var value) &&
            value.ValueKind != JsonValueKind.Null &&
            !TryTimestamp(value, out _))
        {
            errors.Add($"{path}.{property} 必须是有效时间戳");
        }
    }

    private static bool TryTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out timestamp);
    }

    private static void RequiredNonBlankString(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (!TryString(owner, property, out var value) || string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path}.{property} 必须是非空字符串");
        }
    }

    private static void RequiredBoolean(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (!TryBoolean(owner, property, out _))
        {
            errors.Add($"{path}.{property} 必须是布尔值");
        }
    }

    private static bool TryBoolean(
        JsonElement owner,
        string property,
        out bool value)
    {
        if (owner.TryGetProperty(property, out var propertyValue) &&
            propertyValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = propertyValue.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static IReadOnlyList<string> StringArray(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (!owner.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.{property} 必须是字符串数组");
            return [];
        }
        var items = value.EnumerateArray().ToArray();
        if (items.Any(item => item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString())))
        {
            errors.Add($"{path}.{property} 必须是字符串数组");
            return [];
        }
        return items.Select(item => item.GetString()!).ToArray();
    }

    private static void RequiredString(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (!owner.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}.{property} 必须是字符串");
        }
    }

    private static bool TryString(
        JsonElement owner,
        string property,
        out string value)
    {
        if (owner.TryGetProperty(property, out var propertyValue) &&
            propertyValue.ValueKind == JsonValueKind.String)
        {
            value = propertyValue.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void OptionalString(JsonElement owner, string property, List<string> errors)
    {
        if (owner.TryGetProperty(property, out var value) &&
            value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add($"{property} 必须是字符串");
        }
    }

    private static void OptionalString(
        JsonElement owner,
        string property,
        string path,
        List<string> errors)
    {
        if (owner.TryGetProperty(property, out var value) &&
            value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add($"{path}.{property} 必须是字符串");
        }
    }

    private sealed record PendingQuestionShape(
        bool Multiple,
        bool AllowsCustom,
        bool IsSecret,
        IReadOnlySet<string> Options);
}
