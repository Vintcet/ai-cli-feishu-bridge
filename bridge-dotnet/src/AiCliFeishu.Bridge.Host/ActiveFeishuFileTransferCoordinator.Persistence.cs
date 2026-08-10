using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuFileTransferCoordinator
{
    private async Task RememberFileRouteAsync(
        string messageId,
        string sessionId,
        string chatId,
        CancellationToken cancellationToken)
    {
        var route = new MessageRouteStoreRecord
        {
            MessageId = messageId,
            SessionId = sessionId,
            ChatId = chatId,
            Kind = BridgeRuntimeNotificationKinds.Stop,
            CreatedAt = clock.GetUtcNow().ToString("O"),
        };
        await storeOwner.UpdateAsync(
            store => AddRoute(store, route),
            cancellationToken);
    }

    private static BridgeStoreSnapshot AddRoute(
        BridgeStoreSnapshot store,
        MessageRouteStoreRecord route)
    {
        var messages = new Dictionary<string, MessageRouteStoreRecord>(
            store.Routes.Messages,
            StringComparer.Ordinal)
        {
            [route.MessageId] = route,
        };
        return store with
        {
            Routes = new()
            {
                Messages = messages,
                ProcessedInbound = new Dictionary<string, string>(
                    store.Routes.ProcessedInbound,
                    StringComparer.Ordinal),
                ExtensionData = CloneExtensions(store.Routes.ExtensionData),
            },
        };
    }


    private static Dictionary<string, JsonElement>? CloneExtensions(
        Dictionary<string, JsonElement>? extensions) =>
        extensions?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
}
