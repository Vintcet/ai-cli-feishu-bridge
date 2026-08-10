using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public interface IBridgeControlTokenProvider
{
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class FileBridgeControlTokenProvider : IBridgeControlTokenProvider
{
    private readonly BridgeJsonStoreRepository repository;

    public FileBridgeControlTokenProvider(BridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        repository = new BridgeJsonStoreRepository(
            options.DataDirectory,
            BridgeStoreAccess.ReadOnly);
    }

    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await repository.ReadControlTokenAsync(cancellationToken))?.Trim() is
                { Length: > 0 } value
                    ? value
                    : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return null;
    }
}

public static partial class BridgeControlApi
{
    public const string ControlTokenHeader = "X-AI-CLI-Feishu-Control-Token";
    public const string TerminalSecretHeader = "X-AI-CLI-Feishu-Terminal-Secret";
    public const string ExpectedHostKindHeader = "X-AI-CLI-Feishu-Expected-Host-Kind";
    public const string ManagementApiVersionHeader = "X-AI-CLI-Feishu-Management-Api-Version";
    public const string ExpectedProcessIdHeader = "X-AI-CLI-Feishu-Expected-Process-Id";

    public static void MapBridgeControlApi(this WebApplication app)
    {
        MapHealthAndControlApi(app);
        MapApprovalControlApi(app);
        MapSessionGroupControlApi(app);
        MapRuntimeControlApi(app);
        MapManagedIngressApi(app);
        MapOpenCodeEndpointApi(app);
        MapShutdownControlApi(app);
    }
}

public static class BridgeHostManagementContract
{
    public const string HostKind = "dotnet";
    public const int ApiVersion = 1;
}
