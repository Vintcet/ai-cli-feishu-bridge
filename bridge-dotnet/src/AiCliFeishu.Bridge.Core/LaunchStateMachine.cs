namespace AiCliFeishu.Bridge.Core;

public static class LaunchStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Launched = "launched";
    public const string Failed = "failed";
    public const string TimedOut = "timeout";
}

public sealed record LaunchRequestState(
    string RequestId,
    string Kind,
    string Runtime,
    string Cwd,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? SessionId = null,
    DateTimeOffset? CompletedAt = null,
    string? ErrorCode = null);

public sealed record LaunchRegistryState(
    IReadOnlyDictionary<string, LaunchRequestState> Requests)
{
    public static LaunchRegistryState Empty { get; } = new(
        new Dictionary<string, LaunchRequestState>(StringComparer.Ordinal));
}

public static class LaunchStateMachine
{
    public static LaunchRegistryState Queue(
        LaunchRegistryState state,
        LaunchRequestState request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status != LaunchStatuses.Pending ||
            request.Kind is not ("new" or "resume") ||
            request.ExpiresAt <= request.CreatedAt ||
            string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.Runtime) ||
            string.IsNullOrWhiteSpace(request.Cwd))
        {
            throw new ArgumentException("启动任务必须是有效的 pending 请求。", nameof(request));
        }
        if (request.Kind == "resume" && string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new ArgumentException("恢复任务必须指定会话。", nameof(request));
        }
        if (state.Requests.ContainsKey(request.RequestId))
        {
            throw new InvalidOperationException($"启动任务 {request.RequestId} 已存在。 ");
        }
        if (!string.IsNullOrWhiteSpace(request.SessionId) && state.Requests.Values.Any(item =>
                item.SessionId == request.SessionId &&
                item.Status is LaunchStatuses.Pending or LaunchStatuses.Claimed))
        {
            throw new InvalidOperationException($"会话 {request.SessionId} 已有活跃启动任务。 ");
        }
        var requests = Copy(state.Requests);
        requests.Add(request.RequestId, request);
        return state with { Requests = requests };
    }

    public static StateTransition<LaunchRegistryState, bool> Claim(
        LaunchRegistryState state,
        string requestId,
        DateTimeOffset now)
    {
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != LaunchStatuses.Pending || now >= request.ExpiresAt)
        {
            return new(state, false);
        }
        var requests = Copy(state.Requests);
        requests[requestId] = request with { Status = LaunchStatuses.Claimed };
        return new(state with { Requests = requests }, true);
    }

    public static LaunchRegistryState Complete(
        LaunchRegistryState state,
        string requestId,
        bool succeeded,
        DateTimeOffset occurredAt,
        string? errorCode = null)
    {
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != LaunchStatuses.Claimed)
        {
            throw new InvalidOperationException($"启动任务 {requestId} 未被领取。 ");
        }
        var requests = Copy(state.Requests);
        requests[requestId] = request with
        {
            Status = succeeded ? LaunchStatuses.Launched : LaunchStatuses.Failed,
            CompletedAt = occurredAt,
            ErrorCode = succeeded ? null : errorCode,
        };
        return state with { Requests = requests };
    }

    public static LaunchRegistryState Expire(
        LaunchRegistryState state,
        DateTimeOffset now)
    {
        var requests = Copy(state.Requests);
        foreach (var request in requests.Values.Where(item =>
                     item.Status == LaunchStatuses.Pending && item.ExpiresAt <= now).ToArray())
        {
            requests[request.RequestId] = request with
            {
                Status = LaunchStatuses.TimedOut,
                CompletedAt = now,
            };
        }
        return state with { Requests = requests };
    }

    private static Dictionary<string, LaunchRequestState> Copy(
        IReadOnlyDictionary<string, LaunchRequestState> source) =>
        new(source, StringComparer.Ordinal);
}
