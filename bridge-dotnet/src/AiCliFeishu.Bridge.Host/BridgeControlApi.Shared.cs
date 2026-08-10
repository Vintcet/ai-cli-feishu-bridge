using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Host;

public static partial class BridgeControlApi
{
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

    private static async ValueTask<bool> IsManagedIngressAuthenticatedAsync(
        HttpRequest request,
        BridgeManagedIngressKind kind,
        JsonElement body,
        IBridgeControlTokenProvider tokenProvider,
        IBridgeManagedTerminalRegistrationDirectory? terminals,
        CancellationToken cancellationToken)
    {
        if (await IsAuthenticatedAsync(request, tokenProvider, cancellationToken))
        {
            return true;
        }
        if (kind is BridgeManagedIngressKind.TerminalRegister or
            BridgeManagedIngressKind.TerminalUnregister ||
            terminals is null ||
            !body.TryGetProperty("managed_terminal_id", out var terminalIdValue) ||
            terminalIdValue.ValueKind is not JsonValueKind.String)
        {
            return false;
        }
        var terminalId = terminalIdValue.GetString();
        var terminalSecret = request.Headers[TerminalSecretHeader].ToString();
        if (string.IsNullOrWhiteSpace(terminalId) ||
            string.IsNullOrWhiteSpace(terminalSecret))
        {
            return false;
        }
        try
        {
            return terminals.IsAuthenticated(terminalId, terminalSecret);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
