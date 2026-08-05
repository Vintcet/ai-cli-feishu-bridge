namespace AiCliFeishu.Bridge.Core;

public sealed record MessageRouteState(
    string MessageId,
    string SessionId,
    string ChatId,
    string Kind,
    DateTimeOffset CreatedAt,
    string? RequestId = null);

public sealed record MessageRouteRegistryState(
    IReadOnlyDictionary<string, MessageRouteState> Messages,
    IReadOnlyDictionary<string, DateTimeOffset> ProcessedInbound)
{
    public static MessageRouteRegistryState Empty { get; } = new(
        new Dictionary<string, MessageRouteState>(StringComparer.Ordinal),
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
}

public sealed record RetentionPolicy(
    TimeSpan RouteRetention,
    TimeSpan ApprovalRetention,
    int MaxMessageRoutes,
    int MaxProcessedInbound,
    int MaxCompletedApprovals)
{
    public static RetentionPolicy Default { get; } = new(
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(1),
        3_000,
        5_000,
        500);
}

public static class MessageRouteStateMachine
{
    public static MessageRouteRegistryState AddRoute(
        MessageRouteRegistryState state,
        MessageRouteState route)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(route);
        if (string.IsNullOrWhiteSpace(route.MessageId) ||
            string.IsNullOrWhiteSpace(route.SessionId) ||
            string.IsNullOrWhiteSpace(route.ChatId) ||
            string.IsNullOrWhiteSpace(route.Kind))
        {
            throw new ArgumentException("消息路由字段不能为空。", nameof(route));
        }
        var messages = new Dictionary<string, MessageRouteState>(state.Messages, StringComparer.Ordinal)
        {
            [route.MessageId] = route,
        };
        return state with { Messages = messages };
    }

    public static StateTransition<MessageRouteRegistryState, bool> ClaimInbound(
        MessageRouteRegistryState state,
        string messageId,
        DateTimeOffset processedAt)
    {
        if (state.ProcessedInbound.ContainsKey(messageId))
        {
            return new(state, false);
        }
        var inbound = new Dictionary<string, DateTimeOffset>(
            state.ProcessedInbound,
            StringComparer.Ordinal)
        {
            [messageId] = processedAt,
        };
        return new(state with { ProcessedInbound = inbound }, true);
    }

    public static MessageRouteRegistryState Prune(
        MessageRouteRegistryState state,
        DateTimeOffset now,
        RetentionPolicy policy)
    {
        ValidatePolicy(policy);
        var cutoff = now - policy.RouteRetention;
        var messages = state.Messages
            .Where(item => item.Value.CreatedAt >= cutoff)
            .OrderBy(item => item.Value.CreatedAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .TakeLast(policy.MaxMessageRoutes)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var inbound = state.ProcessedInbound
            .Where(item => item.Value >= cutoff)
            .OrderBy(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .TakeLast(policy.MaxProcessedInbound)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return state with { Messages = messages, ProcessedInbound = inbound };
    }

    internal static void ValidatePolicy(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.RouteRetention < TimeSpan.Zero ||
            policy.ApprovalRetention < TimeSpan.Zero ||
            policy.MaxMessageRoutes < 0 ||
            policy.MaxProcessedInbound < 0 ||
            policy.MaxCompletedApprovals < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }
}
