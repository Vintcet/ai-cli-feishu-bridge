namespace AiCliFeishu.Bridge.Core;

public static class ApprovalStatuses
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Orphaned = "orphaned";
}

public static class ApprovalResolutions
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Local = "local";
    public const string Timeout = "timeout";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Allow, Deny, Local, Timeout],
        StringComparer.Ordinal);
}

public sealed record ApprovalState(
    string RequestId,
    string SessionId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> MessageIds,
    string? Resolution = null,
    DateTimeOffset? ResolvedAt = null,
    string TurnId = "",
    string Cwd = "",
    string ToolName = "",
    string ToolPreview = "");

public sealed record ApprovalRegistryState(
    IReadOnlyDictionary<string, ApprovalState> Requests,
    IReadOnlySet<string> Claims)
{
    public static ApprovalRegistryState Empty { get; } = new(
        new Dictionary<string, ApprovalState>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public static class ApprovalStateMachine
{
    public static ApprovalRegistryState Create(
        ApprovalRegistryState state,
        ApprovalState approval)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Status != ApprovalStatuses.Pending ||
            approval.ExpiresAt <= approval.CreatedAt ||
            string.IsNullOrWhiteSpace(approval.RequestId) ||
            string.IsNullOrWhiteSpace(approval.SessionId))
        {
            throw new ArgumentException("新审批必须是具有有效期限的 pending 请求。", nameof(approval));
        }
        if (state.Requests.ContainsKey(approval.RequestId))
        {
            throw new InvalidOperationException($"审批 {approval.RequestId} 已存在。 ");
        }
        var requests = CopyRequests(state.Requests);
        requests.Add(approval.RequestId, approval with
        {
            MessageIds = approval.MessageIds.Distinct(StringComparer.Ordinal).ToArray(),
        });
        return state with { Requests = requests };
    }

    public static ApprovalRegistryState AddMessage(
        ApprovalRegistryState state,
        string requestId,
        string messageId)
    {
        var approval = Pending(state, requestId);
        if (approval.MessageIds.Contains(messageId, StringComparer.Ordinal))
        {
            return state;
        }
        var requests = CopyRequests(state.Requests);
        requests[requestId] = approval with
        {
            MessageIds = [.. approval.MessageIds, messageId],
        };
        return state with { Requests = requests };
    }

    public static StateTransition<ApprovalRegistryState, bool> Claim(
        ApprovalRegistryState state,
        string requestId)
    {
        if (!state.Requests.TryGetValue(requestId, out var approval) ||
            approval.Status != ApprovalStatuses.Pending ||
            state.Claims.Contains(requestId))
        {
            return new(state, false);
        }
        var claims = CopyClaims(state.Claims);
        claims.Add(requestId);
        return new(state with { Claims = claims }, true);
    }

    public static ApprovalRegistryState ReleaseClaim(
        ApprovalRegistryState state,
        string requestId)
    {
        if (!state.Claims.Contains(requestId))
        {
            return state;
        }
        var claims = CopyClaims(state.Claims);
        claims.Remove(requestId);
        return state with { Claims = claims };
    }

    public static StateTransition<ApprovalRegistryState, bool> ResolveClaimed(
        ApprovalRegistryState state,
        string requestId,
        string resolution,
        DateTimeOffset resolvedAt)
    {
        if (!state.Claims.Contains(requestId) ||
            !state.Requests.TryGetValue(requestId, out var approval) ||
            approval.Status != ApprovalStatuses.Pending)
        {
            return new(ReleaseClaim(state, requestId), false);
        }
        return Resolve(state, approval, resolution, resolvedAt);
    }

    public static StateTransition<ApprovalRegistryState, bool> ResolveExternally(
        ApprovalRegistryState state,
        string requestId,
        string resolution,
        DateTimeOffset resolvedAt)
    {
        if (!state.Requests.TryGetValue(requestId, out var approval) ||
            approval.Status != ApprovalStatuses.Pending)
        {
            return new(state, false);
        }
        return Resolve(state, approval, resolution, resolvedAt);
    }

    public static StateTransition<ApprovalRegistryState, int> RecoverPending(
        ApprovalRegistryState state,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pending = state.Requests.Values
            .Where(approval => approval.Status == ApprovalStatuses.Pending)
            .ToArray();
        if (pending.Length == 0)
        {
            return new(state, 0);
        }

        var requests = CopyRequests(state.Requests);
        foreach (var approval in pending)
        {
            requests[approval.RequestId] = approval with
            {
                Status = ApprovalStatuses.Orphaned,
                Resolution = ApprovalResolutions.Local,
                ResolvedAt = observedAt < approval.CreatedAt
                    ? approval.CreatedAt
                    : observedAt,
            };
        }
        return new(
            state with
            {
                Requests = requests,
                Claims = new HashSet<string>(StringComparer.Ordinal),
            },
            pending.Length);
    }

    private static StateTransition<ApprovalRegistryState, bool> Resolve(
        ApprovalRegistryState state,
        ApprovalState approval,
        string resolution,
        DateTimeOffset resolvedAt)
    {
        if (!ApprovalResolutions.All.Contains(resolution))
        {
            throw new ArgumentException($"不支持的审批结果 {resolution}。", nameof(resolution));
        }
        if (resolvedAt < approval.CreatedAt)
        {
            throw new InvalidOperationException("审批完成时间不能早于创建时间。 ");
        }
        var requests = CopyRequests(state.Requests);
        requests[approval.RequestId] = approval with
        {
            Status = ApprovalStatuses.Resolved,
            Resolution = resolution,
            ResolvedAt = resolvedAt,
        };
        var claims = CopyClaims(state.Claims);
        claims.Remove(approval.RequestId);
        return new(state with { Requests = requests, Claims = claims }, true);
    }

    private static ApprovalState Pending(ApprovalRegistryState state, string requestId)
    {
        if (!state.Requests.TryGetValue(requestId, out var approval) ||
            approval.Status != ApprovalStatuses.Pending)
        {
            throw new InvalidOperationException($"审批 {requestId} 不存在或已结束。 ");
        }
        return approval;
    }

    private static Dictionary<string, ApprovalState> CopyRequests(
        IReadOnlyDictionary<string, ApprovalState> source) =>
        new(source, StringComparer.Ordinal);

    private static HashSet<string> CopyClaims(IReadOnlySet<string> source) =>
        new(source, StringComparer.Ordinal);
}
