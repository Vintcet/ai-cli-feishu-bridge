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
