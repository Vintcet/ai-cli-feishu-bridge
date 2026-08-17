using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal static class SessionGroupNameSynchronizer
{
    public static async Task<string?> SynchronizeAsync(
        SessionStoreRecord session,
        IBridgeActiveSessionGroupStateOwner stateOwner,
        IFeishuGateway gateway,
        CancellationToken cancellationToken)
    {
        var chatId = ExtensionString(session, "feishuChatId");
        if (chatId is null)
        {
            return null;
        }

        var name = SessionGroupNameRules.Build(
            session.Runtime,
            ExtensionString(session, "alias"),
            session.ProjectName,
            string.IsNullOrWhiteSpace(session.ShortId)
                ? ShortId(session.SessionId)
                : session.ShortId.Trim(),
            ExtensionPositiveInt(session, "feishuChatOrdinal"));
        if (string.Equals(
                ExtensionString(session, "feishuChatName"),
                name,
                StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            await gateway.UpdateSessionGroupNameAsync(
                chatId,
                name,
                cancellationToken);
            var update = await stateOwner.UpdateSessionGroupNameAsync(
                session.SessionId,
                chatId,
                name,
                cancellationToken);
            return update.Succeeded
                ? null
                : update.Error ?? "状态保存失败，请稍后重试。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "请稍后重试。";
        }
    }

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static int? ExtensionPositiveInt(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var number) &&
        number > 0
            ? number
            : null;

    private static string ShortId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[^8..];
}
