using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

public interface IBridgeControlTokenProvider
{
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class FileBridgeControlTokenProvider(BridgeHostOptions options)
    : IBridgeControlTokenProvider
{
    private readonly string path = Path.Combine(options.DataDirectory, "control-token.json");

    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4_096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("token", out var token) &&
                token.ValueKind is JsonValueKind.String)
            {
                return token.GetString()?.Trim() is { Length: > 0 } value ? value : null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return null;
    }
}

public static class BridgeControlApi
{
    public const string ControlTokenHeader = "X-AI-CLI-Feishu-Control-Token";
    public const string ExpectedHostKindHeader = "X-AI-CLI-Feishu-Expected-Host-Kind";
    public const string ManagementApiVersionHeader = "X-AI-CLI-Feishu-Management-Api-Version";
    public const string ExpectedProcessIdHeader = "X-AI-CLI-Feishu-Expected-Process-Id";

    public static void MapBridgeControlApi(this WebApplication app)
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
            return Results.Ok(status.Snapshot());
        });

        MapSessionGroupControlApi(app);

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
            return result.Ok
                ? Results.Ok(result)
                : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
        });

        MapManagedIngress(
            app,
            "/managed-terminals/register",
            BridgeManagedIngressKind.TerminalRegister);
        MapManagedIngress(
            app,
            "/managed-terminals/unregister",
            BridgeManagedIngressKind.TerminalUnregister);
        MapManagedIngress(app, "/hooks/session-start", BridgeManagedIngressKind.SessionStart);
        MapManagedIngress(app, "/hooks/session-end", BridgeManagedIngressKind.SessionEnd);
        MapManagedIngress(app, "/hooks/permission", BridgeManagedIngressKind.Permission);
        MapManagedIngress(
            app,
            "/hooks/request-user-input",
            BridgeManagedIngressKind.RequestUserInput);
        MapManagedIngress(app, "/hooks/activity", BridgeManagedIngressKind.Activity);
        MapManagedIngress(app, "/hooks/stop", BridgeManagedIngressKind.Stop);

        MapOpenCodeEndpointApi(app);

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

    internal static void MapOpenCodeEndpointApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(
            "/opencode/launch",
            (Func<HttpContext, Task<IResult>>)HandleOpenCodeLaunchAsync);
        MapOpenCodeEndpointIngress(app, "/opencode/register", register: true);
        MapOpenCodeEndpointIngress(app, "/opencode/unregister", register: false);
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
                new { },
                statusCode: StatusCodes.Status400BadRequest);
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
        try
        {
            var result = await ingress.HandleAsync(
                kind,
                body.Value,
                context.TraceIdentifier,
                cancellationToken);
            return Results.Json(result);
        }
        catch (Exception error) when (
            error is InvalidDataException or ArgumentException or JsonException)
        {
            return Results.Json(
                new { },
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException)
        {
            return Results.Json(
                new ControlError(false, "托管终端或会话身份不存在。"),
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException)
        {
            return Results.Json(
                new ControlError(false, "托管终端 Hook 当前不可处理。"),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static bool IsCrossSite(HttpRequest request)
    {
        var value = request.Headers["Sec-Fetch-Site"].ToString();
        return value.Length > 0 && value is not "same-origin" and not "none";
    }

    private static bool HasExpectedManagementIdentity(HttpRequest request) =>
        request.Headers[ExpectedHostKindHeader].ToString() == BridgeHostManagementContract.HostKind &&
        int.TryParse(request.Headers[ManagementApiVersionHeader], out var apiVersion) &&
        apiVersion == BridgeHostManagementContract.ApiVersion &&
        int.TryParse(request.Headers[ExpectedProcessIdHeader], out var processId) &&
        processId == Environment.ProcessId;

    private static async ValueTask<bool> IsAuthenticatedAsync(
        HttpRequest request,
        IBridgeControlTokenProvider tokenProvider,
        CancellationToken cancellationToken)
    {
        var expected = await tokenProvider.ReadAsync(cancellationToken);
        var actual = request.Headers[ControlTokenHeader].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
        {
            return false;
        }
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool HasApplicationJsonContentType(HttpRequest request)
    {
        var value = request.ContentType;
        return value is not null &&
            string.Equals(
                value.Split(';', 2)[0].Trim(),
                "application/json",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadPort(JsonElement value, out int port)
    {
        port = 0;
        if (!value.TryGetProperty("port", out var property))
        {
            return false;
        }
        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var number) =>
                number,
            _ => 0,
        };
        if (parsed is <= 0 or > 65_535)
        {
            return false;
        }
        port = parsed;
        return true;
    }

    private static async ValueTask<ManagedJsonReadResult> ReadLimitedJsonObjectAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
        {
            return new(ManagedJsonReadStatus.TooLarge, default);
        }
        await using var buffer = new MemoryStream(
            request.ContentLength > 0 && request.ContentLength <= maximumBytes
                ? (int)request.ContentLength.Value
                : 0);
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(
                chunk.AsMemory(),
                cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximumBytes)
            {
                return new(ManagedJsonReadStatus.TooLarge, default);
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length == 0)
        {
            return new(ManagedJsonReadStatus.Invalid, default);
        }
        try
        {
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                cancellationToken: cancellationToken);
            return document.RootElement.ValueKind is JsonValueKind.Object
                ? new(ManagedJsonReadStatus.Valid, document.RootElement.Clone())
                : new(ManagedJsonReadStatus.Invalid, default);
        }
        catch (JsonException)
        {
            return new(ManagedJsonReadStatus.Invalid, default);
        }
    }

    private static async ValueTask<JsonElement?> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);
            return document.RootElement.ValueKind is JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PublicBridgeHealth(bool Ok);

    private sealed record ControlAccepted(bool Ok);

    private sealed record ControlError(bool Ok, string Error);

    private sealed record RuntimeLaunchClaimResult(
        bool Ok,
        BridgeManagedRuntimeLaunchRequest? Request);

    private enum ManagedJsonReadStatus
    {
        Valid,
        Invalid,
        TooLarge,
    }

    private readonly record struct ManagedJsonReadResult(
        ManagedJsonReadStatus Status,
        JsonElement Value);
}

public static class BridgeHostManagementContract
{
    public const string HostKind = "dotnet";
    public const int ApiVersion = 1;
}
