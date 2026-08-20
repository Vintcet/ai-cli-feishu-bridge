using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
    private const int MaximumSettingsBodyBytes = 16 * 1024;

    private static void MapSettingsControlApi(WebApplication app) =>
        app.MapPost(
            "/settings",
            (Func<HttpContext, Task<IResult>>)HandleSettingsUpdateAsync);

    private static async Task<IResult> HandleSettingsUpdateAsync(HttpContext context)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
        if (!HasApplicationJsonContentType(request))
        {
            return Results.Json(
                new ControlError(false, "请求必须使用 application/json。"),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }
        if (IsCrossSite(request))
        {
            return Results.Json(
                new ControlError(false, "拒绝跨站请求。"),
                statusCode: StatusCodes.Status403Forbidden);
        }
        var tokenProvider = context.RequestServices
            .GetRequiredService<IBridgeControlTokenProvider>();
        if (!await IsAuthenticatedAsync(request, tokenProvider, cancellationToken))
        {
            return Results.Json(
                new ControlError(false, "本机控制令牌无效。"),
                statusCode: StatusCodes.Status401Unauthorized);
        }
        var options = context.RequestServices.GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return Results.Json(
                new ControlError(false, "Passive Host 不保存设置。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var payload = await ReadLimitedJsonObjectAsync(
            request,
            MaximumSettingsBodyBytes,
            cancellationToken);
        if (payload.Status is ManagedJsonReadStatus.TooLarge)
        {
            return Results.Json(
                new ControlError(false, "设置请求过大。"),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        if (payload.Status is not ManagedJsonReadStatus.Valid)
        {
            return Results.Json(
                new ControlError(false, "请求格式不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!TryParseSettingsPatch(
                payload.Value,
                out var patch,
                out var validationError))
        {
            return Results.Json(
                new ControlError(false, validationError ?? "请求格式不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        SettingsStoreDocument? updated = null;
        try
        {
            await context.RequestServices
                .GetRequiredService<IBridgeProductionStoreOwner>()
                .UpdateAsync(
                    store =>
                    {
                        updated = ApplySettingsPatch(store.Settings, patch!);
                        return store with { Settings = updated };
                    },
                    cancellationToken);
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            JsonException or
            InvalidDataException)
        {
            return Results.Json(
                new ControlError(false, "设置暂时无法保存，请稍后重试。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return updated is null
            ? Results.Json(
                new ControlError(false, "设置暂时无法保存，请稍后重试。"),
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new SettingsUpdateResponse(true, updated));
    }

    private static bool TryParseSettingsPatch(
        JsonElement payload,
        out SettingsPatch? patch,
        out string? error)
    {
        patch = null;
        error = null;
        var count = 0;

        if (!TryOptionalWorkspace(
                payload,
                "workspaceRoot",
                ref count,
                out var workspaceRoot,
                out error) ||
            !TryOptionalBoolean(
                payload,
                "notifyActivity",
                ref count,
                out var notifyActivity,
                out error) ||
            !TryOptionalBoolean(
                payload,
                "notifyUserPrompts",
                ref count,
                out var notifyUserPrompts,
                out error) ||
            !TryOptionalBoolean(
                payload,
                "autoRetryErrors",
                ref count,
                out var autoRetryErrors,
                out error) ||
            !TryOptionalInteger(
                payload,
                "retryMaxAttempts",
                BridgeSettingsLimits.RetryMaxAttemptsMinimum,
                BridgeSettingsLimits.RetryMaxAttemptsMaximum,
                ref count,
                out var retryMaxAttempts,
                out error) ||
            !TryOptionalInteger(
                payload,
                "retryIntervalSeconds",
                1,
                600,
                ref count,
                out var retryIntervalSeconds,
                out error) ||
            !TryOptionalInteger(
                payload,
                "retryJitterSeconds",
                0,
                120,
                ref count,
                out var retryJitterSeconds,
                out error) ||
            !TryOptionalBoolean(
                payload,
                "autoApprove",
                ref count,
                out var autoApprove,
                out error) ||
            !TryOptionalBoolean(
                payload,
                "notifyAutoApprovals",
                ref count,
                out var notifyAutoApprovals,
                out error))
        {
            return false;
        }
        if (count == 0)
        {
            error = "没有可保存的设置。";
            return false;
        }

        patch = new(
            workspaceRoot,
            notifyActivity,
            notifyUserPrompts,
            autoRetryErrors,
            retryMaxAttempts,
            retryIntervalSeconds,
            retryJitterSeconds,
            autoApprove,
            notifyAutoApprovals);
        return true;
    }

    private static bool TryOptionalWorkspace(
        JsonElement payload,
        string name,
        ref int count,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!payload.TryGetProperty(name, out var property))
        {
            return true;
        }
        count++;
        if (property.ValueKind is not JsonValueKind.String ||
            property.GetString() is not { } raw ||
            string.IsNullOrWhiteSpace(raw) ||
            raw.Length > 1_024)
        {
            error = "默认工作区必须是有效的绝对目录。";
            return false;
        }
        try
        {
            var trimmed = raw.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                error = "默认工作区必须是有效的绝对目录。";
                return false;
            }
            var fullPath = Path.GetFullPath(trimmed);
            if (!Directory.Exists(fullPath))
            {
                error = File.Exists(fullPath)
                    ? "默认工作区不是文件夹。"
                    : "默认工作区不存在或无法访问。";
                return false;
            }
            value = fullPath;
            return true;
        }
        catch (Exception pathError) when (
            pathError is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException)
        {
            error = "默认工作区必须是有效的绝对目录。";
            return false;
        }
    }

    private static bool TryOptionalBoolean(
        JsonElement payload,
        string name,
        ref int count,
        out bool? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!payload.TryGetProperty(name, out var property))
        {
            return true;
        }
        count++;
        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            error = "设置值必须是开关状态。";
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryOptionalInteger(
        JsonElement payload,
        string name,
        int minimum,
        int maximum,
        ref int count,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!payload.TryGetProperty(name, out var property))
        {
            return true;
        }
        count++;
        if (property.ValueKind is not JsonValueKind.Number ||
            !property.TryGetInt32(out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            error = $"{name} 必须是 {minimum} 到 {maximum} 之间的整数。";
            return false;
        }
        value = parsed;
        return true;
    }

    private static SettingsStoreDocument ApplySettingsPatch(
        SettingsStoreDocument current,
        SettingsPatch patch) => new()
    {
        WorkspaceRoot = patch.WorkspaceRoot ?? current.WorkspaceRoot,
        NotifyActivity = patch.NotifyActivity ?? current.NotifyActivity,
        NotifyUserPrompts = patch.NotifyUserPrompts ?? current.NotifyUserPrompts,
        AutoRetryErrors = patch.AutoRetryErrors ?? current.AutoRetryErrors,
        RetryMaxAttempts = patch.RetryMaxAttempts ?? current.RetryMaxAttempts,
        RetryIntervalSeconds =
            patch.RetryIntervalSeconds ?? current.RetryIntervalSeconds,
        RetryJitterSeconds = patch.RetryJitterSeconds ?? current.RetryJitterSeconds,
        AutoApprove = patch.AutoApprove ?? current.AutoApprove,
        NotifyAutoApprovals =
            patch.NotifyAutoApprovals ?? current.NotifyAutoApprovals,
        ExtensionData = current.ExtensionData,
    };

    private sealed record SettingsPatch(
        string? WorkspaceRoot,
        bool? NotifyActivity,
        bool? NotifyUserPrompts,
        bool? AutoRetryErrors,
        int? RetryMaxAttempts,
        int? RetryIntervalSeconds,
        int? RetryJitterSeconds,
        bool? AutoApprove,
        bool? NotifyAutoApprovals);

    private sealed record SettingsUpdateResponse(
        bool Ok,
        SettingsStoreDocument Settings);
}
