using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
    private const int MaximumSessionManagementBodyBytes = 16 * 1024;

    internal static void MapSessionManagementControlApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(
            "/sessions/alias",
            (Func<HttpContext, Task<IResult>>)HandleSessionAliasUpdateAsync);
        app.MapPost(
            "/sessions/history/hide",
            (Func<HttpContext, Task<IResult>>)HandleSessionHistoryHideAsync);
    }

    private static async Task<IResult> HandleSessionAliasUpdateAsync(
        HttpContext context)
    {
        var validation = await ReadSessionManagementBodyAsync(context);
        if (validation.Error is not null)
        {
            return validation.Error;
        }
        var body = validation.Body;
        var sessionId = SessionId(body);
        var hasAlias = body.TryGetProperty("alias", out var aliasValue);
        if (sessionId is null || !hasAlias ||
            aliasValue.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            return Results.Json(
                new ControlError(false, "会话 ID 或别名参数不完整。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var alias = aliasValue.ValueKind is JsonValueKind.String
            ? aliasValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(alias))
        {
            alias = null;
        }
        BridgeSessionAliasUpdateResult update;
        try
        {
            var owner = context.RequestServices
                .GetRequiredService<IBridgeActiveSessionAliasStateOwner>();
            update = await owner.UpdateSessionAliasAsync(
                sessionId,
                alias,
                context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsSessionManagementStateFailure(error))
        {
            return Results.Json(
                new ControlError(false, "会话别名暂时无法保存，请稍后重试。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (update.Conflict is not null)
        {
            var normalized = alias is null
                ? string.Empty
                : SessionAliasRules.Normalize(alias);
            return Results.Json(
                new ControlError(
                    false,
                    $"别名 @{normalized} 已被会话 " +
                    $"{ProjectLabel(update.Conflict)} 使用。"),
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!update.Succeeded || update.Session is null)
        {
            return Results.Json(
                new ControlError(false, update.Error ?? "设置别名失败。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var warning = await SessionGroupNameSynchronizer.SynchronizeAsync(
            update.Session,
            context.RequestServices
                .GetRequiredService<IBridgeActiveSessionGroupStateOwner>(),
            context.RequestServices.GetRequiredService<IFeishuGateway>(),
            context.RequestAborted);
        return Results.Ok(new
        {
            ok = true,
            session = new
            {
                sessionId = update.Session.SessionId,
                shortId = SessionShortId(update.Session),
                alias = ExtensionString(update.Session, "alias") ?? string.Empty,
            },
            warning,
        });
    }

    private static async Task<IResult> HandleSessionHistoryHideAsync(
        HttpContext context)
    {
        var validation = await ReadSessionManagementBodyAsync(context);
        if (validation.Error is not null)
        {
            return validation.Error;
        }
        var sessionId = SessionId(validation.Body);
        if (sessionId is null)
        {
            return Results.Json(
                new ControlError(false, "会话 ID 参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        BridgeSessionHistoryHideResult result;
        try
        {
            result = await context.RequestServices
                .GetRequiredService<IBridgeActiveSessionHistoryStateOwner>()
                .HideSessionFromHistoryAsync(sessionId, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsSessionManagementStateFailure(error))
        {
            return Results.Json(
                new ControlError(false, "历史记录暂时无法删除，请稍后重试。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return result.Succeeded && result.Session is not null
            ? Results.Ok(new { ok = true, sessionId = result.Session.SessionId })
            : Results.Json(
                new ControlError(false, result.Error ?? "删除历史记录失败。"),
                statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<SessionManagementBody> ReadSessionManagementBodyAsync(
        HttpContext context)
    {
        var request = context.Request;
        if (!HasApplicationJsonContentType(request))
        {
            return new(default, Results.Json(
                new ControlError(false, "请求必须使用 application/json。"),
                statusCode: StatusCodes.Status415UnsupportedMediaType));
        }
        if (IsCrossSite(request))
        {
            return new(default, Results.Json(
                new ControlError(false, "拒绝跨站请求。"),
                statusCode: StatusCodes.Status403Forbidden));
        }
        var tokenProvider = context.RequestServices
            .GetRequiredService<IBridgeControlTokenProvider>();
        if (!await IsAuthenticatedAsync(
                request,
                tokenProvider,
                context.RequestAborted))
        {
            return new(default, Results.Json(
                new ControlError(false, "本机控制令牌无效。"),
                statusCode: StatusCodes.Status401Unauthorized));
        }
        var options = context.RequestServices.GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return new(default, Results.Json(
                new ControlError(false, "Passive Host 不修改会话。"),
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }
        var business = context.RequestServices
            .GetRequiredService<IBridgeControlBusinessStateSource>();
        if (!business.Snapshot.Initialized)
        {
            return new(default, Results.Json(
                new ControlError(false, "业务状态尚未从 Store 初始化。"),
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        var body = await ReadLimitedJsonObjectAsync(
            request,
            MaximumSessionManagementBodyBytes,
            context.RequestAborted);
        if (body.Status is ManagedJsonReadStatus.TooLarge)
        {
            return new(default, Results.Json(
                new ControlError(false, "请求体不能超过 16 KiB。"),
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }
        return body.Status is ManagedJsonReadStatus.Valid
            ? new(body.Value, null)
            : new(default, Results.Json(
                new ControlError(false, "请求格式不正确。"),
                statusCode: StatusCodes.Status400BadRequest));
    }

    private static string? SessionId(JsonElement body) =>
        body.TryGetProperty("sessionId", out var value) &&
        value.ValueKind is JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 and <= 256 } sessionId
            ? sessionId
            : null;

    private static string ProjectLabel(SessionStoreRecord session) =>
        $"{session.ProjectName ?? session.Cwd} #{SessionShortId(session)}";

    private static string SessionShortId(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.ShortId)
            ? session.SessionId.Length <= 8
                ? session.SessionId
                : session.SessionId[^8..]
            : session.ShortId.Trim();

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static bool IsSessionManagementStateFailure(Exception error) =>
        error is IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException;

    private sealed record SessionManagementBody(JsonElement Body, IResult? Error);
}
