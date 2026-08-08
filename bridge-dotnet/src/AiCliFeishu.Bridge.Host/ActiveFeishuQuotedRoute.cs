using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed record ActiveFeishuQuotedRoute(
    string MessageId,
    MessageRouteStoreRecord Route);

internal static class ActiveFeishuQuotedRouteLookup
{
    public static ActiveFeishuQuotedRoute? Find(
        FeishuIntent intent,
        RouteStoreDocument routes)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(routes);

        foreach (var parameterName in new[] { "parentMessageId", "rootMessageId" })
        {
            var messageId = Parameter(intent, parameterName);
            if (messageId is not null &&
                routes.Messages.TryGetValue(messageId, out var route))
            {
                return new(messageId, route);
            }
        }
        return null;
    }

    private static string? Parameter(FeishuIntent intent, string name)
    {
        if (intent.Parameters?.TryGetValue(name, out var value) != true)
        {
            return null;
        }
        if (value is null)
        {
            return null;
        }
        var normalized = value.Trim();
        return normalized.Length is > 0 and <= 256 &&
            !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }
}
