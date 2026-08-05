namespace AiCliFeishu.Bridge.Core;

public static class SessionStatuses
{
    public const string Starting = "starting";
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string PendingApproval = "pending_approval";
    public const string PendingInput = "pending_input";
    public const string LocalApproval = "local_approval";
    public const string Error = "error";
    public const string Ended = "ended";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Starting, Ready, Running, Waiting, PendingApproval, PendingInput,
            LocalApproval, Error, Ended],
        StringComparer.Ordinal);
}

public sealed record SessionState(
    string SessionId,
    string Runtime,
    string Cwd,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? EndedAt = null,
    string? LastError = null);

public sealed record SessionDirectoryState(
    IReadOnlyDictionary<string, SessionState> Sessions)
{
    public static SessionDirectoryState Empty { get; } = new(
        new Dictionary<string, SessionState>(StringComparer.Ordinal));
}

public static class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [SessionStatuses.Starting] = Set(
                SessionStatuses.Ready, SessionStatuses.Running, SessionStatuses.Waiting,
                SessionStatuses.PendingApproval, SessionStatuses.PendingInput,
                SessionStatuses.Error, SessionStatuses.Ended),
            [SessionStatuses.Ready] = ActiveTargets(),
            [SessionStatuses.Running] = ActiveTargets(),
            [SessionStatuses.Waiting] = ActiveTargets(),
            [SessionStatuses.PendingApproval] = ActiveTargets(),
            [SessionStatuses.PendingInput] = ActiveTargets(),
            [SessionStatuses.LocalApproval] = ActiveTargets(),
            [SessionStatuses.Error] = Set(
                SessionStatuses.Starting, SessionStatuses.Ready, SessionStatuses.Running,
                SessionStatuses.Waiting, SessionStatuses.Ended),
            [SessionStatuses.Ended] = Set(SessionStatuses.Starting),
        };

    public static SessionDirectoryState Register(
        SessionDirectoryState state,
        SessionState session)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(session);
        ValidateSession(session);
        if (state.Sessions.ContainsKey(session.SessionId))
        {
            throw new InvalidOperationException($"会话 {session.SessionId} 已存在。 ");
        }
        var sessions = Copy(state.Sessions);
        sessions.Add(session.SessionId, session);
        return state with { Sessions = sessions };
    }

    public static SessionDirectoryState Transition(
        SessionDirectoryState state,
        string sessionId,
        string nextStatus,
        DateTimeOffset occurredAt,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Sessions.TryGetValue(sessionId, out var current))
        {
            throw new KeyNotFoundException($"会话 {sessionId} 不存在。 ");
        }
        if (!SessionStatuses.All.Contains(nextStatus))
        {
            throw new ArgumentException($"不支持的会话状态 {nextStatus}。", nameof(nextStatus));
        }
        if (occurredAt < current.LastSeenAt)
        {
            throw new InvalidOperationException("会话事件时间不能早于当前状态。 ");
        }
        if (!string.Equals(current.Status, nextStatus, StringComparison.Ordinal) &&
            !Allowed[current.Status].Contains(nextStatus))
        {
            throw new InvalidOperationException(
                $"会话不能从 {current.Status} 转换到 {nextStatus}。 ");
        }

        var reopening = current.Status == SessionStatuses.Ended &&
            nextStatus == SessionStatuses.Starting;
        var next = current with
        {
            Status = nextStatus,
            LastSeenAt = occurredAt,
            OpenedAt = reopening ? occurredAt : current.OpenedAt,
            EndedAt = nextStatus == SessionStatuses.Ended
                ? occurredAt
                : reopening ? null : current.EndedAt,
            LastError = nextStatus == SessionStatuses.Error
                ? error ?? current.LastError
                : null,
        };
        var sessions = Copy(state.Sessions);
        sessions[sessionId] = next;
        return state with { Sessions = sessions };
    }

    private static void ValidateSession(SessionState session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) ||
            string.IsNullOrWhiteSpace(session.Runtime) ||
            string.IsNullOrWhiteSpace(session.Cwd))
        {
            throw new ArgumentException("会话标识、运行时和目录不能为空。", nameof(session));
        }
        if (!SessionStatuses.All.Contains(session.Status))
        {
            throw new ArgumentException($"不支持的会话状态 {session.Status}。", nameof(session));
        }
    }

    private static HashSet<string> ActiveTargets() => Set(
        SessionStatuses.Ready, SessionStatuses.Running, SessionStatuses.Waiting,
        SessionStatuses.PendingApproval, SessionStatuses.PendingInput,
        SessionStatuses.LocalApproval, SessionStatuses.Error, SessionStatuses.Ended);

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.Ordinal);

    private static Dictionary<string, SessionState> Copy(
        IReadOnlyDictionary<string, SessionState> source) =>
        new(source, StringComparer.Ordinal);
}
