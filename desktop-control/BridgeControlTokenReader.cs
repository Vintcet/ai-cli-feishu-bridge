using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

internal static class BridgeControlTokenReader
{
    public static bool TryRead(string bridgeRoot, out string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeRoot);
        token = "";
        try
        {
            var repository = new BridgeJsonStoreRepository(
                Path.Combine(bridgeRoot, "data"),
                BridgeStoreAccess.ReadOnly);
            var candidate = Task.Run(async () =>
                    await repository.ReadControlTokenAsync())
                .GetAwaiter()
                .GetResult()
                ?.Trim();
            if (!IsValid(candidate))
            {
                return false;
            }
            token = candidate!;
            return true;
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            BridgeStoreValidationException)
        {
            return false;
        }
    }

    public static string Read(string bridgeRoot)
    {
        if (TryRead(bridgeRoot, out var token))
        {
            return token;
        }
        throw new InvalidOperationException(
            "找不到有效的本机控制令牌。请确认 Bridge Host 已成功启动。");
    }

    private static bool IsValid(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);
}
