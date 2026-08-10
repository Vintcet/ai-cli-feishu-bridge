using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
    internal static void MapApprovalControlApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(
            "/approvals/resolve",
            (Func<HttpContext, Task<IResult>>)HandleLocalApprovalResolveAsync);
    }

    private static async Task<IResult> HandleLocalApprovalResolveAsync(
        HttpContext context)
    {
        const int maximumBodyBytes = 1024 * 1024;
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
        var body = await ReadLimitedJsonObjectAsync(
            request,
            maximumBodyBytes,
            cancellationToken);
        if (body.Status is ManagedJsonReadStatus.TooLarge)
        {
            return Results.Json(
                new ControlError(false, "请求体不能超过 1 MiB。"),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        if (body.Status is not ManagedJsonReadStatus.Valid)
        {
            return Results.Json(
                new ControlError(false, "请求格式不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var requestId = body.Value.TryGetProperty("requestId", out var requestValue) &&
            requestValue.ValueKind is JsonValueKind.String
                ? requestValue.GetString()?.Trim()
                : null;
        var resolution = body.Value.TryGetProperty("resolution", out var resolutionValue) &&
            resolutionValue.ValueKind is JsonValueKind.String
                ? resolutionValue.GetString()?.Trim()
                : null;
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ||
            resolution is not ApprovalResolutions.Allow and not ApprovalResolutions.Deny)
        {
            return Results.Json(
                new ControlError(false, "审批请求或处理方式不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var options = context.RequestServices.GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return Results.Json(
                new ControlError(false, "Passive Host 不处理本机审批。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var businessState = context.RequestServices
            .GetRequiredService<IBridgeControlBusinessStateSource>();
        if (!businessState.Snapshot.Initialized)
        {
            return Results.Json(
                new ControlError(false, "业务状态尚未从 Store 初始化。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var stateOwner = context.RequestServices
            .GetRequiredService<IBridgeActiveApprovalStateOwner>();
        var current = stateOwner.Snapshot;
        current.Approvals.Requests.TryGetValue(requestId, out var approval);
        if (approval is null || approval.Status != ApprovalStatuses.Pending)
        {
            return Results.Ok(AlreadyResolvedApproval(approval));
        }

        if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            try
            {
                var expired = await stateOwner.ExpireApprovalAsync(
                    requestId,
                    cancellationToken);
                var observed = expired ?? stateOwner.Snapshot.Approvals.Requests
                    .GetValueOrDefault(requestId);
                if (observed is null || observed.Status != ApprovalStatuses.Pending)
                {
                    if (observed is not null)
                    {
                        await SynchronizeApprovalBestEffortAsync(
                            context.RequestServices,
                            observed,
                            cancellationToken);
                    }
                    return Results.Ok(AlreadyResolvedApproval(observed));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (IsApprovalStateFailure(error))
            {
                return Results.Json(
                    new ControlError(false, "本机审批状态暂不可用，请刷新后重试。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        try
        {
            var store = await context.RequestServices
                .GetRequiredService<IBridgeProductionStoreOwner>()
                .ReadAsync(cancellationToken);
            var result = await context.RequestServices
                .GetRequiredService<ActiveFeishuApprovalCoordinator>()
                .HandleLocalAsync(
                    requestId,
                    resolution,
                    store,
                    cancellationToken);
            return result.Ok
                ? Results.Ok(result)
                : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsApprovalStateFailure(error))
        {
            var observed = stateOwner.Snapshot.Approvals.Requests
                .GetValueOrDefault(requestId);
            return observed is null || observed.Status != ApprovalStatuses.Pending
                ? Results.Ok(AlreadyResolvedApproval(observed))
                : Results.Json(
                    new ControlError(false, "处理本机审批失败，请刷新后重试。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task ExpirePendingApprovalsBestEffortAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var stateOwner = context.RequestServices
            .GetService<IBridgeActiveApprovalStateOwner>();
        var current = stateOwner?.Snapshot;
        if (stateOwner is null || current is null || !current.Initialized)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var approval in current.Approvals.Requests.Values.Where(item =>
                     item.Status == ApprovalStatuses.Pending &&
                     item.ExpiresAt <= now).ToArray())
        {
            try
            {
                var expired = await stateOwner.ExpireApprovalAsync(
                    approval.RequestId,
                    cancellationToken);
                if (expired is not null)
                {
                    await SynchronizeApprovalBestEffortAsync(
                        context.RequestServices,
                        expired,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (IsApprovalStateFailure(error))
            {
                // Status polling must remain available even if one stale
                // approval cannot be persisted or its Feishu card cannot be patched.
            }
        }
    }

    private static async Task SynchronizeApprovalBestEffortAsync(
        IServiceProvider services,
        ApprovalState approval,
        CancellationToken cancellationToken)
    {
        var notifier = services.GetService<IBridgeActiveApprovalNotifier>();
        if (notifier is null)
        {
            return;
        }
        try
        {
            await notifier.SynchronizeAsync(
                approval.RequestId,
                approval.SessionId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The durable terminal state is authoritative. The existing
            // notification synchronizer will retry card patches later.
        }
    }

    private static BridgeLocalApprovalResolveResult AlreadyResolvedApproval(
        ApprovalState? approval) => new(
            true,
            true,
            approval?.Resolution ?? ApprovalResolutions.Local,
            "这条审批已经处理或失效。");

    private static bool IsApprovalStateFailure(Exception error) =>
        error is IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            HttpRequestException;

    internal static void MapSessionGroupControlApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(
            "/sessions/feishu-group/retry",
            (Func<HttpContext, Task<IResult>>)HandleSessionGroupRetryAsync);
    }

    private static async Task<IResult> HandleSessionGroupRetryAsync(
        HttpContext context)
    {
        const int maximumBodyBytes = 1024 * 1024;
        var request = context.Request;
        var cancellationToken = request.HttpContext.RequestAborted;
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

        var body = await ReadLimitedJsonObjectAsync(
            request,
            maximumBodyBytes,
            cancellationToken);
        if (body.Status is ManagedJsonReadStatus.TooLarge)
        {
            return Results.Json(
                new ControlError(false, "请求体不能超过 1 MiB。"),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        if (body.Status is not ManagedJsonReadStatus.Valid)
        {
            return Results.Json(
                new ControlError(false, "请求格式不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var sessionId = body.Value.TryGetProperty("sessionId", out var sessionValue) &&
            sessionValue.ValueKind is JsonValueKind.String
                ? sessionValue.GetString()?.Trim()
                : null;
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256)
        {
            return Results.Json(
                new ControlError(false, "会话 ID 参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var options = context.RequestServices
            .GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return Results.Json(
                new ControlError(false, "Passive Host 不重试会话群。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var businessState = context.RequestServices
            .GetRequiredService<IBridgeControlBusinessStateSource>();
        if (!businessState.Snapshot.Initialized)
        {
            return Results.Json(
                new ControlError(false, "业务状态尚未从 Store 初始化。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var coordinator = context.RequestServices
                .GetRequiredService<IBridgeActiveSessionGroupCoordinator>();
            var result = await coordinator.RetryAsync(
                sessionId,
                cancellationToken);
            if (!result.Succeeded)
            {
                return Results.Json(
                    new ControlError(false, result.Error ?? "飞书群创建失败，请重试。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(new
            {
                ok = true,
                alreadyConnected = result.AlreadyConnected,
                chatId = result.ChatId,
                chatName = result.ChatName ?? string.Empty,
            });
        }
        catch (InvalidOperationException)
        {
            return Results.Json(
                new ControlError(false, "Active 会话群协调器当前不可用。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
