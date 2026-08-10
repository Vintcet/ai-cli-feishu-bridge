using System.Globalization;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal sealed class PersistentFeishuInboundDeduplicator(
    IBridgeProductionStoreOwner storeOwner,
    TimeProvider? timeProvider = null) : IFeishuInboundDeduplicator
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public bool TryClaim(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }
        var claimed = false;
        storeOwner.UpdateAsync(store =>
        {
            if (store.Routes.ProcessedInbound.ContainsKey(eventId))
            {
                return store;
            }
            claimed = true;
            var inbound = new Dictionary<string, string>(
                store.Routes.ProcessedInbound,
                StringComparer.Ordinal)
            {
                [eventId] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            };
            return store with
            {
                Routes = new RouteStoreDocument
                {
                    Messages = store.Routes.Messages,
                    ProcessedInbound = inbound,
                    ExtensionData = store.Routes.ExtensionData,
                },
            };
        }).AsTask().GetAwaiter().GetResult();
        return claimed;
    }

    public void Release(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }
        storeOwner.UpdateAsync(store =>
        {
            if (!store.Routes.ProcessedInbound.ContainsKey(eventId))
            {
                return store;
            }
            var inbound = new Dictionary<string, string>(
                store.Routes.ProcessedInbound,
                StringComparer.Ordinal);
            inbound.Remove(eventId);
            return store with
            {
                Routes = new RouteStoreDocument
                {
                    Messages = store.Routes.Messages,
                    ProcessedInbound = inbound,
                    ExtensionData = store.Routes.ExtensionData,
                },
            };
        }).AsTask().GetAwaiter().GetResult();
    }
}
