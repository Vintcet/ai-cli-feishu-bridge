namespace AiCliFeishu.Bridge.Core;

public static class RetryStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Exhausted = "exhausted";
    public const string Cancelled = "cancelled";
}

public sealed record RetryTaskState(
    string RetryId,
    string SessionId,
    int Attempt,
    int MaxAttempts,
    string Status,
    DateTimeOffset DueAt,
    DateTimeOffset UpdatedAt);

public sealed record RetryRegistryState(
    IReadOnlyDictionary<string, RetryTaskState> Tasks)
{
    public static RetryRegistryState Empty { get; } = new(
        new Dictionary<string, RetryTaskState>(StringComparer.Ordinal));
}

public static class RetryStateMachine
{
    public static RetryRegistryState Schedule(
        RetryRegistryState state,
        RetryTaskState task)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != RetryStatuses.Pending ||
            task.Attempt < 1 || task.MaxAttempts < task.Attempt ||
            string.IsNullOrWhiteSpace(task.RetryId) ||
            string.IsNullOrWhiteSpace(task.SessionId))
        {
            throw new ArgumentException("重试任务必须是有效的 pending 尝试。", nameof(task));
        }
        if (state.Tasks.Values.Any(item =>
            item.SessionId == task.SessionId &&
            item.Status is RetryStatuses.Pending or RetryStatuses.Claimed))
        {
            throw new InvalidOperationException($"会话 {task.SessionId} 已有活跃重试。 ");
        }
        var tasks = Copy(state.Tasks);
        tasks.Add(task.RetryId, task);
        return state with { Tasks = tasks };
    }

    public static StateTransition<RetryRegistryState, bool> ClaimDue(
        RetryRegistryState state,
        string retryId,
        DateTimeOffset now)
    {
        if (!state.Tasks.TryGetValue(retryId, out var task) ||
            task.Status != RetryStatuses.Pending || task.DueAt > now)
        {
            return new(state, false);
        }
        var tasks = Copy(state.Tasks);
        tasks[retryId] = task with { Status = RetryStatuses.Claimed, UpdatedAt = now };
        return new(state with { Tasks = tasks }, true);
    }

    public static RetryRegistryState Complete(
        RetryRegistryState state,
        string retryId,
        bool succeeded,
        DateTimeOffset occurredAt)
    {
        if (!state.Tasks.TryGetValue(retryId, out var task) ||
            task.Status != RetryStatuses.Claimed)
        {
            throw new InvalidOperationException($"重试 {retryId} 未被领取。 ");
        }
        var status = succeeded
            ? RetryStatuses.Completed
            : task.Attempt >= task.MaxAttempts
                ? RetryStatuses.Exhausted
                : RetryStatuses.Failed;
        var tasks = Copy(state.Tasks);
        tasks[retryId] = task with { Status = status, UpdatedAt = occurredAt };
        return state with { Tasks = tasks };
    }

    public static RetryRegistryState CancelSession(
        RetryRegistryState state,
        string sessionId,
        DateTimeOffset occurredAt)
    {
        var tasks = Copy(state.Tasks);
        foreach (var task in tasks.Values.Where(item =>
                     item.SessionId == sessionId &&
                     item.Status is RetryStatuses.Pending or RetryStatuses.Claimed).ToArray())
        {
            tasks[task.RetryId] = task with
            {
                Status = RetryStatuses.Cancelled,
                UpdatedAt = occurredAt,
            };
        }
        return state with { Tasks = tasks };
    }

    private static Dictionary<string, RetryTaskState> Copy(
        IReadOnlyDictionary<string, RetryTaskState> source) =>
        new(source, StringComparer.Ordinal);
}
