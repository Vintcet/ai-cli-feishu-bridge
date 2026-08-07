using System.Globalization;
using System.Text.Json;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public static class NodeStoreBusinessStateMerger
{
    public static NodeStoreSnapshot Merge(
        NodeStoreSnapshot store,
        SessionDirectoryState sessions,
        ApprovalRegistryState approvals)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(approvals);
        return store with
        {
            Sessions = MergeSessions(store.Sessions, sessions),
            Approvals = MergeApprovals(store.Approvals, approvals),
        };
    }

    private static SessionStoreDocument MergeSessions(
        SessionStoreDocument document,
        SessionDirectoryState state)
    {
        var records = state.Sessions.ToDictionary(
            item => item.Key,
            item => MergeSession(
                document.Sessions.GetValueOrDefault(item.Key),
                item.Value),
            StringComparer.Ordinal);
        return new SessionStoreDocument
        {
            Sessions = records,
            ExtensionData = Clone(document.ExtensionData),
        };
    }

    private static SessionStoreRecord MergeSession(
        SessionStoreRecord? current,
        SessionState state) => new()
    {
        SessionId = state.SessionId,
        ShortId = current?.ShortId ?? ShortSessionId(state.SessionId),
        Cwd = state.Cwd,
        ProjectName = current?.ProjectName ?? ProjectName(state.Cwd),
        Status = state.Status,
        Runtime = state.Runtime,
        OpenedAt = Timestamp(current?.OpenedAt, state.OpenedAt),
        LastSeenAt = Timestamp(current?.LastSeenAt, state.LastSeenAt),
        EndedAt = OptionalTimestamp(current?.EndedAt, state.EndedAt),
        LastError = state.LastError,
        ExtensionData = Clone(current?.ExtensionData),
    };

    private static ApprovalStoreDocument MergeApprovals(
        ApprovalStoreDocument document,
        ApprovalRegistryState state)
    {
        var records = state.Requests.ToDictionary(
            item => item.Key,
            item => MergeApproval(
                document.Requests.GetValueOrDefault(item.Key),
                item.Value),
            StringComparer.Ordinal);
        return new ApprovalStoreDocument
        {
            Requests = records,
            ExtensionData = Clone(document.ExtensionData),
        };
    }

    private static ApprovalStoreRecord MergeApproval(
        ApprovalStoreRecord? current,
        ApprovalState state) => new()
    {
        RequestId = state.RequestId,
        SessionId = state.SessionId,
        TurnId = ValueOrExisting(state.TurnId, current?.TurnId),
        Cwd = ValueOrExisting(state.Cwd, current?.Cwd),
        ToolName = ValueOrExisting(state.ToolName, current?.ToolName),
        ToolPreview = ValueOrExisting(state.ToolPreview, current?.ToolPreview),
        CreatedAt = Timestamp(current?.CreatedAt, state.CreatedAt),
        ExpiresAt = Timestamp(current?.ExpiresAt, state.ExpiresAt),
        Status = state.Status,
        MessageIds = state.MessageIds.Distinct(StringComparer.Ordinal).ToList(),
        Resolution = state.Resolution,
        ResolvedAt = OptionalTimestamp(current?.ResolvedAt, state.ResolvedAt),
        ExtensionData = Clone(current?.ExtensionData),
    };

    private static string ValueOrExisting(string value, string? existing) =>
        string.IsNullOrEmpty(value) ? existing ?? string.Empty : value;

    private static string Timestamp(string? existing, DateTimeOffset value) =>
        existing is not null &&
        DateTimeOffset.TryParse(
            existing,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) &&
        parsed.Equals(value)
            ? existing
            : value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? OptionalTimestamp(
        string? existing,
        DateTimeOffset? value) =>
        value is null ? null : Timestamp(existing, value.Value);

    private static string ShortSessionId(string sessionId)
    {
        var compact = new string(sessionId
            .Where(character =>
                character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9')
            .ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }

    private static string ProjectName(string cwd)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(cwd);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    private static Dictionary<string, JsonElement>? Clone(
        Dictionary<string, JsonElement>? extensionData) =>
        extensionData?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
}
