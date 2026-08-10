using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal static class BridgeStoreRetention
{
    private static readonly TimeSpan EndedSessionRetention = TimeSpan.FromDays(90);

    public static BridgeStoreSnapshot PruneRoutes(
        BridgeStoreSnapshot store,
        DateTimeOffset now)
    {
        var cutoff = now - RetentionPolicy.Default.RouteRetention;
        var messages = store.Routes.Messages
            .Where(item => Timestamp(item.Value.CreatedAt) >= cutoff)
            .OrderBy(item => Timestamp(item.Value.CreatedAt))
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .TakeLast(RetentionPolicy.Default.MaxMessageRoutes)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var inbound = store.Routes.ProcessedInbound
            .Where(item => Timestamp(item.Value) >= cutoff)
            .OrderBy(item => Timestamp(item.Value))
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .TakeLast(RetentionPolicy.Default.MaxProcessedInbound)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (messages.Count == store.Routes.Messages.Count &&
            inbound.Count == store.Routes.ProcessedInbound.Count)
        {
            return store;
        }
        return store with
        {
            Routes = new RouteStoreDocument
            {
                Messages = messages,
                ProcessedInbound = inbound,
                ExtensionData = store.Routes.ExtensionData,
            },
        };
    }

    public static SessionDirectoryState PruneEndedSessions(
        SessionDirectoryState sessions,
        DateTimeOffset now)
    {
        var cutoff = now - EndedSessionRetention;
        var retained = sessions.Sessions
            .Where(item => item.Value.Status != SessionStatuses.Ended ||
                (item.Value.EndedAt ?? item.Value.LastSeenAt) >= cutoff)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return retained.Count == sessions.Sessions.Count
            ? sessions
            : sessions with { Sessions = retained };
    }

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : DateTimeOffset.MinValue;
}
