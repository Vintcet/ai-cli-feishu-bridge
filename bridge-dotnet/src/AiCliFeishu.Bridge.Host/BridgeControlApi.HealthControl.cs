using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
    private static void MapHealthAndControlApi(WebApplication app)
    {
        app.MapGet("/health", async (
            HttpRequest request,
            BridgeHealthRegistry health,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
            var authenticated = await IsAuthenticatedAsync(request, tokenProvider, cancellationToken);
            return authenticated
                ? Results.Ok(health.Snapshot())
                : Results.Ok(new PublicBridgeHealth(true));
        });

        app.MapGet("/control/status", async (
            HttpRequest request,
            BridgeControlStatusReader status,
            IBridgeControlStoreStatusSource storeStatus,
            IBridgeControlBusinessStateSource businessState,
            BridgeHealthRegistry health,
            IBridgeControlTokenProvider tokenProvider,
            CancellationToken cancellationToken) =>
        {
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
            if (request.Query["refresh"].ToString() == "1")
            {
                await storeStatus.RefreshAsync(cancellationToken);
                await businessState.RefreshAsync(cancellationToken);
                var component = storeStatus.ComponentHealth;
                health.Report(component.Name, component.Status, component.Detail);
                var businessComponent = businessState.ComponentHealth;
                health.Report(
                    businessComponent.Name,
                    businessComponent.Status,
                    businessComponent.Detail);
            }
            await ExpirePendingApprovalsBestEffortAsync(
                request.HttpContext,
                cancellationToken);
            return Results.Ok(status.Snapshot());
        });

        app.MapGet(
            "/control/desktop-presence",
            (Func<HttpContext, Task<IResult>>)HandleDesktopPresenceAsync);
        app.MapPost(
            "/hooks/local-presence",
            (Func<HttpContext, Task<IResult>>)HandleLocalPresenceAsync);
    }

    private static void MapShutdownControlApi(WebApplication app)
    {
        app.MapPost("/control/shutdown", async (
            HttpContext context,
            IBridgeControlTokenProvider tokenProvider,
            IHostApplicationLifetime lifetime,
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
            if (!await IsAuthenticatedAsync(context.Request, tokenProvider, cancellationToken))
            {
                return Results.Json(
                    new ControlError(false, "本机控制令牌无效。"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            if (!HasExpectedManagementIdentity(context.Request))
            {
                return Results.Json(
                    new ControlError(false, "目标 Bridge Host 身份不匹配，已拒绝停止。"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            context.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted(value: new ControlAccepted(true));
        });
    }

    private static async Task<IResult> HandleDesktopPresenceAsync(HttpContext context)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
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
                new ControlError(false, "Passive Host 不提供桌面在线会话投影。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var store = await context.RequestServices
                .GetRequiredService<IBridgeProductionStoreProjectionReader>()
                .ReadForProjectionAsync(cancellationToken);
            var heartbeats = context.RequestServices
                .GetRequiredService<BridgeDesktopSessionHeartbeatDirectory>();
            var presence = BridgeDesktopSessionPresenceProjection.Project(
                store,
                context.RequestServices.GetRequiredService<
                    AiCliFeishu.Bridge.Adapters.ManagedTerminal.IManagedTerminalDirectory>(),
                context.RequestServices.GetRequiredService<
                    AiCliFeishu.Bridge.Adapters.OpenCode.IOpenCodeEndpointDirectory>(),
                DateTimeOffset.UtcNow,
                heartbeatProbe: heartbeats.IsOnline,
                sessionActiveLifetime:
                    BridgeLocalConfiguration.SessionActiveLifetime(options));
            return Results.Ok(presence);
        }
        catch (Exception error) when (
            error is IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException)
        {
            return Results.Json(
                new ControlError(false, "桌面在线会话状态暂不可用。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleLocalPresenceAsync(HttpContext context)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
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
                new ControlError(false, "Passive Host 不接受本机会话在线登记。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        JsonElement payload;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Results.Json(
                new ControlError(false, "本机会话在线登记 JSON 无效。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        BridgeDesktopSessionHeartbeat heartbeat;
        try
        {
            (heartbeat, _) = context.RequestServices
                .GetRequiredService<BridgeDesktopSessionHeartbeatDirectory>()
                .Record(payload);
        }
        catch (InvalidDataException error)
        {
            return Results.Json(
                new ControlError(false, error.Message),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            await context.RequestServices
                .GetRequiredService<IBridgeProductionStoreOwner>()
                .UpdateAsync(
                    store => PersistDesktopHeartbeat(store, heartbeat),
                    CancellationToken.None);
        }
        catch (Exception error) when (
            error is IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException)
        {
            // The in-memory heartbeat is enough for the desktop projection.
            // Persistence is best-effort so a degraded Store never blocks a
            // local-only Hook or turns it into a Feishu workflow.
        }
        return Results.Ok(new ControlAccepted(true));
    }

    private static BridgeStoreSnapshot PersistDesktopHeartbeat(
        BridgeStoreSnapshot store,
        BridgeDesktopSessionHeartbeat heartbeat)
    {
        if (!store.Sessions.Sessions.TryGetValue(heartbeat.SessionId, out var session) ||
            DesktopHeartbeatMatches(session, heartbeat))
        {
            return store;
        }
        return BridgeStoreBusinessStateMerger.PatchSessionExtensions(
            store,
            heartbeat.SessionId,
            new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
            {
                ["clientProcessId"] = JsonSerializer.SerializeToElement(
                    heartbeat.ClientProcessId),
                ["clientProcessStartedAt"] = heartbeat.ClientProcessStartedAt is null
                    ? null
                    : JsonSerializer.SerializeToElement(
                        heartbeat.ClientProcessStartedAt),
            });
    }

    private static bool DesktopHeartbeatMatches(
        SessionStoreRecord session,
        BridgeDesktopSessionHeartbeat heartbeat)
    {
        if (session.ExtensionData is null)
        {
            return false;
        }
        var process = session.ExtensionData.FirstOrDefault(item => string.Equals(
            item.Key,
            "clientProcessId",
            StringComparison.OrdinalIgnoreCase));
        if (process.Value.ValueKind is not JsonValueKind.Number ||
            !process.Value.TryGetInt32(out var processId) ||
            processId != heartbeat.ClientProcessId)
        {
            return false;
        }
        var started = session.ExtensionData.FirstOrDefault(item => string.Equals(
            item.Key,
            "clientProcessStartedAt",
            StringComparison.OrdinalIgnoreCase));
        var currentStartedAt = started.Value.ValueKind is JsonValueKind.String
            ? started.Value.GetString()
            : null;
        return string.Equals(
            currentStartedAt,
            heartbeat.ClientProcessStartedAt,
            StringComparison.Ordinal);
    }
}
