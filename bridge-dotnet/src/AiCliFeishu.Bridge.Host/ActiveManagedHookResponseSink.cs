using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveManagedHookResponseSink(
    BridgeHostOptions options,
    IBridgeManagedTerminalRegistrationDirectory terminals,
    ManagedRuntimeHookBridge hookBridge) : IManagedHookResponseSink
{
    public bool IsReady(string runtime, string sessionExternalId)
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active ||
            runtime is not RuntimeNames.Codex and not RuntimeNames.ClaudeCode ||
            string.IsNullOrWhiteSpace(sessionExternalId) ||
            sessionExternalId.Length > 256 ||
            sessionExternalId.Any(char.IsControl))
        {
            return false;
        }
        var identity = terminals.FindClaimBySession(sessionExternalId);
        return (identity is null ||
                string.Equals(identity.Runtime, runtime, StringComparison.Ordinal)) &&
            hookBridge.IsReady(runtime, sessionExternalId);
    }

    public Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        runtime = RequireRuntime(runtime);
        sessionExternalId = RequireIdentifier(
            sessionExternalId,
            nameof(sessionExternalId));
        requestId = RequireIdentifier(requestId, nameof(requestId));
        if (decision is not "allow_once" and not "allow_session" and not "deny")
        {
            throw new InvalidDataException($"未知的审批决定 {decision}。");
        }
        EnsureBoundRuntime(runtime, sessionExternalId);
        return hookBridge.ResolveApprovalAsync(
            context,
            runtime,
            sessionExternalId,
            requestId,
            decision,
            cancellationToken);
    }

    public Task ResolveInputAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(answers);
        cancellationToken.ThrowIfCancellationRequested();
        runtime = RequireRuntime(runtime);
        sessionExternalId = RequireIdentifier(
            sessionExternalId,
            nameof(sessionExternalId));
        requestId = RequireIdentifier(requestId, nameof(requestId));
        EnsureBoundRuntime(runtime, sessionExternalId);
        return hookBridge.ResolveInputAsync(
            context,
            runtime,
            sessionExternalId,
            requestId,
            CloneAnswers(answers),
            cancellationToken);
    }

    private void EnsureBoundRuntime(string runtime, string sessionExternalId)
    {
        var identity = terminals.FindClaimBySession(sessionExternalId);
        if (identity is not null &&
            !string.Equals(identity.Runtime, runtime, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "托管 Hook 响应与当前终端会话的运行时身份不一致。");
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "托管 Hook 生产响应只能用于 Active Host。");
        }
    }

    private static string RequireRuntime(string runtime)
    {
        if (runtime is not RuntimeNames.Codex and not RuntimeNames.ClaudeCode)
        {
            throw new ArgumentException("托管 Hook 运行时无效。", nameof(runtime));
        }
        return runtime;
    }

    private static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("托管 Hook 交互身份无效。", parameterName);
        }
        return value;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CloneAnswers(
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var clone = new Dictionary<string, IReadOnlyList<string>>(
            answers.Count,
            StringComparer.Ordinal);
        foreach (var (questionId, values) in answers)
        {
            _ = RequireIdentifier(questionId, nameof(answers));
            ArgumentNullException.ThrowIfNull(values);
            if (values.Any(answer => answer is null))
            {
                throw new ArgumentException("托管 Hook 答案不能包含 null。", nameof(answers));
            }
            clone.Add(questionId, values.ToArray());
        }
        return clone;
    }
}
