namespace AiCliFeishu.Bridge.Core;

public static class ApprovalRetention
{
    public static ApprovalRegistryState Prune(
        ApprovalRegistryState state,
        DateTimeOffset now,
        RetentionPolicy policy)
    {
        MessageRouteStateMachine.ValidatePolicy(policy);
        var cutoff = now - policy.ApprovalRetention;
        var pending = state.Requests.Values
            .Where(item => item.Status == ApprovalStatuses.Pending && item.ExpiresAt >= cutoff);
        var completed = state.Requests.Values
            .Where(item => item.Status != ApprovalStatuses.Pending &&
                (item.ResolvedAt ?? item.CreatedAt) >= cutoff)
            .OrderBy(item => item.ResolvedAt ?? item.CreatedAt)
            .ThenBy(item => item.RequestId, StringComparer.Ordinal)
            .TakeLast(policy.MaxCompletedApprovals);
        var requests = pending.Concat(completed).ToDictionary(
            item => item.RequestId,
            item => item,
            StringComparer.Ordinal);
        var claims = state.Claims
            .Where(requests.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);
        if (requests.Count == state.Requests.Count &&
            requests.Keys.All(state.Requests.ContainsKey) &&
            claims.SetEquals(state.Claims))
        {
            return state;
        }
        return state with { Requests = requests, Claims = claims };
    }
}
