using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
    private static void MapManagedIngressApi(WebApplication app)
    {
        MapManagedIngress(
            app,
            "/managed-terminals/register",
            BridgeManagedIngressKind.TerminalRegister);
        MapManagedIngress(
            app,
            "/managed-terminals/unregister",
            BridgeManagedIngressKind.TerminalUnregister);
        app.MapGet(
            "/managed-terminals/{terminalId}/status",
            (Func<HttpContext, Task<IResult>>)HandleManagedTerminalStatusAsync);
        MapManagedIngress(app, "/hooks/session-start", BridgeManagedIngressKind.SessionStart);
        MapManagedIngress(app, "/hooks/session-end", BridgeManagedIngressKind.SessionEnd);
        MapManagedIngress(app, "/hooks/permission", BridgeManagedIngressKind.Permission);
        MapManagedIngress(
            app,
            "/hooks/request-user-input",
            BridgeManagedIngressKind.RequestUserInput);
        MapManagedIngress(app, "/hooks/activity", BridgeManagedIngressKind.Activity);
        MapManagedIngress(app, "/hooks/stop", BridgeManagedIngressKind.Stop);
    }

    internal static async Task<IResult> HandleManagedTerminalStatusAsync(
        HttpContext context)
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
        var terminalId = context.Request.RouteValues["terminalId"]?.ToString() ?? "";
        var terminals = context.RequestServices
            .GetService<IBridgeManagedTerminalRegistrationDirectory>();
        if (terminals is null)
        {
            return Results.Json(
                new ControlError(false, "托管终端目录当前不可用。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            var status = terminals.GetStatus(terminalId);
            return Results.Ok(new
            {
                ok = true,
                terminalId,
                registered = status is not null,
                online = status?.Online ?? false,
                ready = status?.Ready ?? false,
                sessionExternalId = status?.SessionExternalId,
                lastSeenAt = status?.LastSeenAt,
            });
        }
        catch (ArgumentException)
        {
            return Results.Json(
                new ControlError(false, "托管终端 ID 无效。"),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException)
        {
            return Results.Json(
                new ControlError(false, "托管终端目录当前不可用。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static void MapManagedIngress(
        WebApplication app,
        string path,
        BridgeManagedIngressKind kind) =>
        app.MapPost(
            path,
            (Func<HttpContext, Task<IResult>>)(context =>
                HandleManagedIngressAsync(context, kind)));

    private static void MapOpenCodeEndpointIngress(
        WebApplication app,
        string path,
        bool register) =>
        app.MapPost(
            path,
            (Func<HttpContext, Task<IResult>>)(context =>
                HandleOpenCodeEndpointIngressAsync(context, register)));

    internal static void MapOpenCodeEndpointApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(
            "/opencode/launch",
            (Func<HttpContext, Task<IResult>>)HandleOpenCodeLaunchAsync);
        app.MapGet(
            "/opencode/endpoints/{port:int}/status",
            (Func<HttpContext, Task<IResult>>)HandleOpenCodeEndpointStatusAsync);
        MapOpenCodeEndpointIngress(app, "/opencode/register", register: true);
        MapOpenCodeEndpointIngress(app, "/opencode/unregister", register: false);
    }

    private static async Task<IResult> HandleOpenCodeEndpointStatusAsync(
        HttpContext context)
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
                new ControlError(false, "Passive Host 不维护 OpenCode 端点。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!int.TryParse(
                context.Request.RouteValues["port"]?.ToString(),
                out var port) || port is <= 0 or > 65_535)
        {
            return Results.Json(
                new ControlError(false, "端口参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }
        var directory = context.RequestServices
            .GetRequiredService<IBridgeOpenCodeEndpointRegistrationDirectory>();
        try
        {
            var identity = directory.FindRegistrationByPort(port);
            return Results.Ok(new
            {
                ok = true,
                port,
                registered = identity is not null,
                ready = identity?.Ready ?? false,
                generation = identity?.Generation ?? 0,
                cwd = identity?.Cwd,
            });
        }
        catch (InvalidOperationException)
        {
            return Results.Json(
                new ControlError(false, "OpenCode 端点目录当前不可处理。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HandleOpenCodeLaunchAsync(
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
        var options = context.RequestServices.GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return Results.Json(
                new ControlError(false, "Passive Host 不预留 OpenCode 端口。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var cwd = body.Value.TryGetProperty("cwd", out var cwdValue) &&
            cwdValue.ValueKind is JsonValueKind.String
                ? cwdValue.GetString()?.Trim()
                : null;
        var hasSession = body.Value.TryGetProperty("sessionId", out var sessionValue);
        var sessionId = hasSession && sessionValue.ValueKind is JsonValueKind.String
            ? sessionValue.GetString()?.Trim()
            : null;
        if (string.IsNullOrEmpty(cwd) || cwd.Length > 1024 ||
            hasSession && sessionValue.ValueKind is not JsonValueKind.String ||
            sessionId?.Length > 512)
        {
            return Results.Json(
                new ControlError(false, "OpenCode 启动参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var lifecycle = context.RequestServices
            .GetRequiredService<IBridgeOpenCodeRuntimeLifecycleOwner>();
        try
        {
            var identity = await lifecycle.ReserveAsync(
                cwd,
                sessionId,
                cancellationToken);
            return Results.Ok(new
            {
                ok = true,
                port = identity.Port,
                cwd = identity.Cwd,
                generation = identity.Generation,
            });
        }
        catch (ArgumentException)
        {
            return Results.Json(
                new ControlError(false, "OpenCode 启动参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException)
        {
            return Results.Json(
                new ControlError(false, "OpenCode 启动端口当前不可用。"),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> HandleOpenCodeEndpointIngressAsync(
        HttpContext context,
        bool register)
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
        if (body.Status is not ManagedJsonReadStatus.Valid ||
            !TryReadPort(body.Value, out var port))
        {
            return Results.Json(
                new ControlError(false, "端口参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var options = context.RequestServices.GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return Results.Json(
                new ControlError(false, "Passive Host 不登记 OpenCode 端点。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var directory = context.RequestServices
            .GetRequiredService<IBridgeOpenCodeEndpointRegistrationDirectory>();
        try
        {
            if (!register)
            {
                context.RequestServices
                    .GetRequiredService<IBridgeOpenCodeRuntimeLifecycleOwner>()
                    .Release(port);
                return Results.Ok(new { ok = true, port });
            }
            var cwd = body.Value.TryGetProperty("cwd", out var cwdValue) &&
                cwdValue.ValueKind is JsonValueKind.String
                    ? cwdValue.GetString()?.Trim()
                    : null;
            if (string.IsNullOrEmpty(cwd) || cwd.Length > 1024)
            {
                return Results.Json(
                    new ControlError(false, "目录参数不正确。"),
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var identity = directory.Register(port, cwd);
            return Results.Ok(new
            {
                ok = true,
                port = identity.Port,
                cwd = identity.Cwd,
            });
        }
        catch (ArgumentException)
        {
            return Results.Json(
                new ControlError(false, register
                    ? "OpenCode 端点参数不正确。"
                    : "端口参数不正确。"),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException)
        {
            return Results.Json(
                new ControlError(false, "OpenCode 端点目录当前不可处理。"),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> HandleManagedIngressAsync(
        HttpContext context,
        BridgeManagedIngressKind kind)
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
        if (string.IsNullOrEmpty(request.Headers[ControlTokenHeader]) &&
            string.IsNullOrEmpty(request.Headers[TerminalSecretHeader]))
        {
            return Results.Json(
                new ControlError(false, "本机控制令牌或托管终端密钥无效。"),
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
                new { },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tokenProvider = context.RequestServices
            .GetRequiredService<IBridgeControlTokenProvider>();
        var terminals = context.RequestServices
            .GetService<IBridgeManagedTerminalRegistrationDirectory>();
        if (!await IsManagedIngressAuthenticatedAsync(
                request,
                kind,
                body.Value,
                tokenProvider,
                terminals,
                cancellationToken))
        {
            return Results.Json(
                new ControlError(false, "本机控制令牌或托管终端密钥无效。"),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var options = context.RequestServices.GetRequiredService<BridgeHostOptions>();
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            return Results.Json(
                new ControlError(false, "Passive Host 不处理托管终端 Hook。"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var ingress = context.RequestServices
            .GetRequiredService<IBridgeManagedHookIngress>();
        var hookLog = context.RequestServices.GetService<IManagedHookRequestLog>();
        var kindName = kind.ToString();
        var sessionId = OptionalLoggedString(body.Value, "session_id");
        var terminalId = OptionalLoggedString(body.Value, "managed_terminal_id");
        try
        {
            var result = await ingress.HandleAsync(
                kind,
                body.Value,
                context.TraceIdentifier,
                cancellationToken);
            await LogManagedIngressAsync(
                hookLog,
                kindName,
                sessionId,
                terminalId,
                StatusCodes.Status200OK,
                null,
                context.TraceIdentifier);
            return Results.Json(result);
        }
        catch (Exception error) when (
            error is InvalidDataException or ArgumentException or JsonException)
        {
            await LogManagedIngressAsync(
                hookLog,
                kindName,
                sessionId,
                terminalId,
                StatusCodes.Status400BadRequest,
                error.Message,
                context.TraceIdentifier);
            return Results.Json(
                new { },
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException error)
        {
            await LogManagedIngressAsync(
                hookLog,
                kindName,
                sessionId,
                terminalId,
                StatusCodes.Status409Conflict,
                error.Message,
                context.TraceIdentifier);
            return Results.Json(
                new ControlError(false, "托管终端或会话身份不存在。"),
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException error)
        {
            await LogManagedIngressAsync(
                hookLog,
                kindName,
                sessionId,
                terminalId,
                StatusCodes.Status409Conflict,
                error.Message,
                context.TraceIdentifier);
            return Results.Json(
                new ControlError(false, "托管终端 Hook 当前不可处理。"),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task LogManagedIngressAsync(
        IManagedHookRequestLog? hookLog,
        string kind,
        string? sessionId,
        string? terminalId,
        int statusCode,
        string? failureReason,
        string traceId)
    {
        if (hookLog is null)
        {
            return;
        }
        // Diagnostics run outside the request's cancellation so a hook that gave up
        // waiting still leaves the reason it failed behind.
        await hookLog.AppendAsync(
            kind,
            sessionId,
            terminalId,
            statusCode,
            failureReason,
            traceId,
            CancellationToken.None);
    }

    private static string? OptionalLoggedString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? ManagedHookRequestLog.Truncate(value.GetString())
            : null;
}
