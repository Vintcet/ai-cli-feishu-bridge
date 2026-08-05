using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
            IBridgeStoreShadow storeShadow,
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
                await storeShadow.RefreshAsync(cancellationToken);
                var component = storeShadow.ComponentHealth;
                health.Report(component.Name, component.Status, component.Detail);
            }
            return Results.Ok(status.Snapshot());
        });

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

    private sealed record PublicBridgeHealth(bool Ok);

    private sealed record ControlAccepted(bool Ok);

    private sealed record ControlError(bool Ok, string Error);
}

public static class BridgeHostManagementContract
{
    public const string HostKind = "dotnet";
    public const int ApiVersion = 1;
}
