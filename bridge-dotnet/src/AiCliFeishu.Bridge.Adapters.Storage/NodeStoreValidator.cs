using System.Text.Json;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public sealed class NodeStoreValidationException(
    string fileName,
    IReadOnlyList<string> errors)
    : IOException($"{fileName} 与 Node Store 结构不兼容：{string.Join("；", errors)}")
{
    public string FileName { get; } = fileName;
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class NodeStoreValidator
{
    public static void Validate(NodeStoreFile file, JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("根节点必须是对象");
            throw new NodeStoreValidationException(file.FileName, errors);
        }

        switch (file.Kind)
        {
            case NodeStoreFileKind.Bindings:
                ValidateObjectMap(root, "users", errors, ValidateBinding);
                OptionalString(root, "ownerOpenId", errors);
                OptionalString(root, "pairingCode", errors);
                break;
            case NodeStoreFileKind.Sessions:
                ValidateObjectMap(root, "sessions", errors, ValidateSession);
                break;
            case NodeStoreFileKind.Routes:
                ValidateOptionalObjectMap(root, "messages", errors, ValidateRoute);
                ValidateOptionalStringMap(root, "processedInbound", errors);
                break;
            case NodeStoreFileKind.Approvals:
                ValidateObjectMap(root, "requests", errors, ValidateApproval);
                break;
            case NodeStoreFileKind.Settings:
            case NodeStoreFileKind.ControlToken:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(file));
        }

        if (errors.Count > 0)
        {
            throw new NodeStoreValidationException(file.FileName, errors);
        }
    }

    private static void ValidateBinding(JsonElement value, string path, List<string> errors)
    {
        RequiredString(value, "openId", path, errors);
        RequiredString(value, "chatId", path, errors);
        RequiredString(value, "chatType", path, errors);
        RequiredString(value, "boundAt", path, errors);
    }

    private static void ValidateSession(JsonElement value, string path, List<string> errors)
    {
        RequiredString(value, "sessionId", path, errors);
        RequiredString(value, "cwd", path, errors);
        RequiredString(value, "status", path, errors);
        RequiredString(value, "lastSeenAt", path, errors);
    }

    private static void ValidateRoute(JsonElement value, string path, List<string> errors)
    {
        RequiredString(value, "messageId", path, errors);
        RequiredString(value, "sessionId", path, errors);
        RequiredString(value, "chatId", path, errors);
        RequiredString(value, "kind", path, errors);
        RequiredString(value, "createdAt", path, errors);
    }

    private static void ValidateApproval(JsonElement value, string path, List<string> errors)
    {
        RequiredString(value, "requestId", path, errors);
        RequiredString(value, "sessionId", path, errors);
        RequiredString(value, "turnId", path, errors);
        RequiredString(value, "cwd", path, errors);
        RequiredString(value, "toolName", path, errors);
        RequiredString(value, "toolPreview", path, errors);
        RequiredString(value, "createdAt", path, errors);
        RequiredString(value, "expiresAt", path, errors);
        RequiredString(value, "status", path, errors);
        if (!value.TryGetProperty("messageIds", out var messageIds) ||
            messageIds.ValueKind != JsonValueKind.Array ||
            messageIds.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            errors.Add($"{path}.messageIds 必须是字符串数组");
        }
    }

    private static void ValidateObjectMap(
        JsonElement root,
        string property,
        List<string> errors,
        Action<JsonElement, string, List<string>> validateItem)
    {
        if (!root.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{property} 必须是对象");
            return;
        }
        ValidateMapItems(map, property, errors, validateItem);
    }

    private static void ValidateOptionalObjectMap(
        JsonElement root,
        string property,
        List<string> errors,
        Action<JsonElement, string, List<string>> validateItem)
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
        ValidateMapItems(map, property, errors, validateItem);
    }

    private static void ValidateMapItems(
        JsonElement map,
        string property,
        List<string> errors,
        Action<JsonElement, string, List<string>> validateItem)
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
        }
    }

    private static void ValidateOptionalStringMap(
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
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{property}.{item.Name} 必须是字符串");
            }
        }
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

    private static void OptionalString(JsonElement owner, string property, List<string> errors)
    {
        if (owner.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{property} 必须是字符串");
        }
    }
}
