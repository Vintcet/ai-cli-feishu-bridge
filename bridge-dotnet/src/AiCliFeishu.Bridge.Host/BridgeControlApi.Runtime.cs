using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
    private static void MapRuntimeControlApi(WebApplication app)
    {
        app.MapPost("/control/runtime-events", async (
            HttpRequest request,
            IRuntimeEventSink runtimeEvents,
            IBridgeControlBusinessStateSource businessState,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
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
            if (!await IsAuthenticatedAsync(request, tokenProvider, cancellationToken))
            {
                return Results.Json(
                    new ControlError(false, "本机控制令牌无效。"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            RuntimeEventEnvelope runtimeEvent;
            try
            {
                runtimeEvent = await JsonSerializer.DeserializeAsync<RuntimeEventEnvelope>(
                    request.Body,
                    BridgeProtocolJson.SerializerOptions,
                    cancellationToken) ?? throw new JsonException("事件不能为空。");
            }
            catch (JsonException)
            {
                return Results.Json(
                    new ControlError(false, "Runtime 事件 JSON 无效。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (!businessState.Snapshot.Initialized)
            {
                return Results.Json(
                    new ControlError(false, "业务状态尚未从 Store 初始化。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                await runtimeEvents.PublishAsync(runtimeEvent, cancellationToken);
            }
            catch (Exception error) when (
                error is InvalidDataException or
                KeyNotFoundException or
                InvalidOperationException or
                ArgumentException)
            {
                return Results.Json(
                    new ControlError(false, "Runtime 事件未通过 Bridge Protocol 或业务顺序校验。"),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            return Results.Accepted(value: new ControlAccepted(true));
        });

        app.MapPost("/control/runtime-commands", async (
            HttpRequest request,
            IBridgeRuntimeCommandGateway commands,
            IBridgeControlBusinessStateSource businessState,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
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
            if (!await IsAuthenticatedAsync(request, tokenProvider, cancellationToken))
            {
                return Results.Json(
                    new ControlError(false, "本机控制令牌无效。"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            RuntimeCommandEnvelope command;
            try
            {
                command = await JsonSerializer.DeserializeAsync<RuntimeCommandEnvelope>(
                    request.Body,
                    BridgeProtocolJson.SerializerOptions,
                    cancellationToken) ?? throw new JsonException("命令不能为空。");
            }
            catch (JsonException)
            {
                return Results.Json(
                    new ControlError(false, "Runtime 命令 JSON 无效。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var validation = BridgeProtocolValidator.Validate(command);
            if (!validation.IsValid)
            {
                return Results.Json(
                    new ControlError(false, "Runtime 命令未通过 Bridge Protocol 校验。"),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            if (!businessState.Snapshot.Initialized)
            {
                return Results.Json(
                    new ControlError(false, "业务状态尚未从 Store 初始化。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                await commands.DispatchAsync(command, cancellationToken);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                return Results.Json(
                    new ControlError(false, "Runtime 命令未通过 Bridge Protocol 校验。"),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (BridgeRuntimeCommandUnavailableException)
            {
                return Results.Json(
                    new ControlError(false, "Runtime 命令执行入口当前不可用。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Accepted(value: new ControlAccepted(true));
        });

        app.MapPost("/control/feishu-intents", async (
            HttpRequest request,
            IFeishuIntentSink intents,
            IBridgeControlBusinessStateSource businessState,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
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
            if (!await IsAuthenticatedAsync(request, tokenProvider, cancellationToken))
            {
                return Results.Json(
                    new ControlError(false, "本机控制令牌无效。"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            FeishuIntent intent;
            try
            {
                intent = await JsonSerializer.DeserializeAsync<FeishuIntent>(
                    request.Body,
                    BridgeProtocolJson.SerializerOptions,
                    cancellationToken) ?? throw new JsonException("意图不能为空。");
            }
            catch (JsonException)
            {
                return Results.Json(
                    new ControlError(false, "飞书标准意图 JSON 无效。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (!businessState.Snapshot.Initialized)
            {
                return Results.Json(
                    new ControlError(false, "业务状态尚未从 Store 初始化。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                var result = await intents.PublishAsync(intent, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                return Results.Json(
                    new ControlError(false, "飞书标准意图未通过边界校验。"),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        app.MapPost("/runtime-launches/claim", async (
            HttpContext context,
            BridgeHostOptions options,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasJsonContentType())
            {
                return Results.Json(
                    new ControlError(false, "请求必须使用 application/json。"),
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            if (IsCrossSite(context.Request))
            {
                return Results.Json(
                    new ControlError(false, "拒绝跨站请求。"),
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (!await IsAuthenticatedAsync(
                    context.Request,
                    tokenProvider,
                    cancellationToken))
            {
                return Results.Json(
                    new ControlError(false, "本机控制令牌无效。"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            if (await ReadJsonObjectAsync(context.Request, cancellationToken) is null)
            {
                return Results.Json(
                    new ControlError(false, "请求格式不正确。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (options.OwnershipMode is not BridgeOwnershipMode.Active)
            {
                return Results.Json(
                    new ControlError(false, "Passive Host 不领取 Runtime 启动请求。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var launches = context.RequestServices
                .GetRequiredService<IBridgeManagedRuntimeLaunchCoordinator>();
            return Results.Ok(new RuntimeLaunchClaimResult(true, launches.Claim()));
        });

        app.MapPost("/runtime-launches/complete", async (
            HttpContext context,
            BridgeHostOptions options,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasJsonContentType())
            {
                return Results.Json(
                    new ControlError(false, "请求必须使用 application/json。"),
                    statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            if (IsCrossSite(context.Request))
            {
                return Results.Json(
                    new ControlError(false, "拒绝跨站请求。"),
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (!await IsAuthenticatedAsync(
                    context.Request,
                    tokenProvider,
                    cancellationToken))
            {
                return Results.Json(
                    new ControlError(false, "本机控制令牌无效。"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            BridgeManagedRuntimeLaunchCompletion completion;
            try
            {
                completion = await JsonSerializer.DeserializeAsync<
                    BridgeManagedRuntimeLaunchCompletion>(
                        context.Request.Body,
                        BridgeProtocolJson.SerializerOptions,
                        cancellationToken) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                return Results.Json(
                    new ControlError(false, "请求格式不正确。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (options.OwnershipMode is not BridgeOwnershipMode.Active)
            {
                return Results.Json(
                    new ControlError(false, "Passive Host 不完成 Runtime 启动请求。"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var launches = context.RequestServices
                .GetRequiredService<IBridgeManagedRuntimeLaunchCoordinator>();
            var result = launches.Complete(completion);
            if (result.Ok &&
                !result.AlreadyResolved &&
                result.SessionId is { Length: > 0 } sessionId)
            {
                await context.RequestServices
                    .GetRequiredService<ActiveRuntimeLaunchNotificationCoordinator>()
                    .CompleteAsync(
                        sessionId,
                        completion.Success is true,
                        result.FailureDetail,
                        cancellationToken);
            }
            return result.Ok
                ? Results.Ok(result)
                : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
        });
    }
}
