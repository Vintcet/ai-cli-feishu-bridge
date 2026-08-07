using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public sealed record NodeStoreCoreState(
    SessionDirectoryState Sessions,
    MessageRouteRegistryState Routes,
    ApprovalRegistryState Approvals);

public static class NodeStoreCoreProjection
{
    public static NodeStoreCoreState Project(NodeStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            ProjectSessions(snapshot.Sessions),
            ProjectRoutes(snapshot.Routes),
            ProjectApprovals(snapshot.Approvals));
    }

    private static SessionDirectoryState ProjectSessions(SessionStoreDocument document)
    {
        var sessions = document.Sessions.ToDictionary(
            item => item.Key,
            item =>
            {
                var value = item.Value;
                return new SessionState(
                    value.SessionId,
                    value.Runtime ?? RuntimeNames.Codex,
                    value.Cwd,
                    value.Status,
                    TimestampOrOldest(value.OpenedAt ?? value.LastSeenAt),
                    TimestampOrOldest(value.LastSeenAt),
                    OptionalTimestampOrOldest(value.EndedAt),
                    value.LastError);
            },
            StringComparer.Ordinal);
        return new SessionDirectoryState(sessions);
    }

    private static MessageRouteRegistryState ProjectRoutes(RouteStoreDocument document)
    {
        var messages = document.Messages.ToDictionary(
            item => item.Key,
            item => new MessageRouteState(
                item.Value.MessageId,
                item.Value.SessionId,
                item.Value.ChatId,
                item.Value.Kind,
                TimestampOrOldest(item.Value.CreatedAt),
                item.Value.RequestId),
            StringComparer.Ordinal);
        var inbound = document.ProcessedInbound.ToDictionary(
            item => item.Key,
            item => TimestampOrOldest(item.Value),
            StringComparer.Ordinal);
        return new MessageRouteRegistryState(messages, inbound);
    }

    private static ApprovalRegistryState ProjectApprovals(ApprovalStoreDocument document)
    {
        var approvals = document.Requests.ToDictionary(
            item => item.Key,
            item => new ApprovalState(
                item.Value.RequestId,
                item.Value.SessionId,
                item.Value.Status,
                TimestampOrOldest(item.Value.CreatedAt),
                TimestampOrOldest(item.Value.ExpiresAt),
                item.Value.MessageIds.ToArray(),
                item.Value.Resolution,
                OptionalTimestampOrOldest(item.Value.ResolvedAt),
                item.Value.TurnId,
                item.Value.Cwd,
                item.Value.ToolName,
                item.Value.ToolPreview),
            StringComparer.Ordinal);
        return new ApprovalRegistryState(
            approvals,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static DateTimeOffset TimestampOrOldest(string value) =>
        DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : DateTimeOffset.MinValue;

    private static DateTimeOffset? OptionalTimestampOrOldest(string? value) =>
        value is null ? null : TimestampOrOldest(value);
}
