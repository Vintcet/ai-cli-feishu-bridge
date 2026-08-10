using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActivePersistentBusinessStateOwner
{
    public async ValueTask<BridgeInputAnswerProgress?> TryRecordInputAnswerAsync(
        string requestId,
        string sessionId,
        string questionId,
        IReadOnlyList<string> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentNullException.ThrowIfNull(answers);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (inputClaims.Contains(requestId) ||
                !TryPendingInput(
                    current,
                    requestId,
                    sessionId,
                    out _,
                    out var session))
            {
                return null;
            }
            var recorded = InputStateMachine.RecordAnswer(
                current.Inputs,
                requestId,
                questionId,
                answers);
            if (!recorded.Value)
            {
                return null;
            }
            var input = recorded.State.Requests[requestId];
            var complete = InputStateMachine.HasCompleteAnswers(input);
            if (complete && !inputClaims.Add(requestId))
            {
                return null;
            }
            var next = current with
            {
                Revision = current.Revision + 1,
                Inputs = recorded.State,
            };
            try
            {
                await PersistAsync(next, cancellationToken);
            }
            catch
            {
                if (complete)
                {
                    inputClaims.Remove(requestId);
                }
                throw;
            }
            Volatile.Write(ref snapshot, next);
            return new(input, session, complete);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeInputClaim?> TryClaimInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!TryPendingInput(
                    current,
                    requestId,
                    sessionId,
                    out var input,
                    out var session) ||
                !inputClaims.Add(requestId))
            {
                return null;
            }
            return new(input, session);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeInputClaim?> ResolveClaimedInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (TryCompletedInput(current, requestId, sessionId, out var completed))
            {
                inputClaims.Remove(requestId);
                return completed;
            }
            if (!TryClaimedInput(
                    current,
                    requestId,
                    sessionId,
                    out var input,
                    out var session) ||
                !InputStateMachine.HasCompleteAnswers(input))
            {
                return null;
            }
            var resolvedAt = Latest(
                clock.GetUtcNow(),
                input.CreatedAt,
                session.LastSeenAt);
            var resolved = InputStateMachine.Answer(
                current.Inputs,
                requestId,
                input.Answers,
                resolvedAt);
            if (!resolved.Value)
            {
                return null;
            }
            var sessions = SessionStateMachine.Transition(
                current.Sessions,
                sessionId,
                SessionStatuses.Running,
                resolvedAt);
            var next = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Inputs = resolved.State,
            };
            await PersistAsync(next, cancellationToken);
            Volatile.Write(ref snapshot, next);
            inputClaims.Remove(requestId);
            return new(
                next.Inputs.Requests[requestId],
                next.Sessions.Sessions[sessionId]);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeInputClaim?> DeferClaimedInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!TryClaimedInput(
                    current,
                    requestId,
                    sessionId,
                    out var input,
                    out var session))
            {
                return null;
            }
            var resolvedAt = Latest(
                clock.GetUtcNow(),
                input.CreatedAt,
                session.LastSeenAt);
            var cleared = InputStateMachine.ClearAnswers(
                current.Inputs,
                requestId);
            var resolved = InputStateMachine.ResolveExternally(
                cleared.State,
                requestId,
                resolvedAt);
            if (!resolved.Value)
            {
                return null;
            }
            var sessions = SessionStateMachine.Transition(
                current.Sessions,
                sessionId,
                session.Runtime == RuntimeNames.OpenCode
                    ? SessionStatuses.PendingInput
                    : SessionStatuses.Waiting,
                resolvedAt);
            var next = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Inputs = resolved.State,
            };
            await PersistAsync(next, cancellationToken);
            Volatile.Write(ref snapshot, next);
            inputClaims.Remove(requestId);
            return new(
                next.Inputs.Requests[requestId],
                next.Sessions.Sessions[sessionId]);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeInputClaim?> ResetClaimedInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (TryCompletedInput(current, requestId, sessionId, out var completed))
            {
                inputClaims.Remove(requestId);
                return completed;
            }
            if (!TryClaimedInput(
                    current,
                    requestId,
                    sessionId,
                    out var input,
                    out var session))
            {
                return null;
            }
            var reset = InputStateMachine.ClearAnswers(current.Inputs, requestId);
            if (reset.Value)
            {
                var next = current with
                {
                    Revision = current.Revision + 1,
                    Inputs = reset.State,
                };
                await PersistAsync(next, cancellationToken);
                Volatile.Write(ref snapshot, next);
                input = reset.State.Requests[requestId];
            }
            inputClaims.Remove(requestId);
            return new(input, session);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask ReleaseInputClaimAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            inputClaims.Remove(requestId);
        }
        finally
        {
            writeGate.Release();
        }
    }


    private bool TryClaimedInput(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out InputRequestState input,
        out SessionState session)
    {
        if (inputClaims.Contains(requestId) &&
            TryPendingInput(current, requestId, sessionId, out input, out session))
        {
            return true;
        }
        input = null!;
        session = null!;
        return false;
    }

    private static bool TryPendingInput(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out InputRequestState input,
        out SessionState session)
    {
        if (current.Inputs.Requests.TryGetValue(requestId, out input!) &&
            input.Status == InputRequestStatuses.Pending &&
            string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        input = null!;
        session = null!;
        return false;
    }

    private static bool TryCompletedInput(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out BridgeInputClaim? completed)
    {
        if (current.Inputs.Requests.TryGetValue(requestId, out var input) &&
            input.Status == InputRequestStatuses.Resolved &&
            string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out var session))
        {
            completed = new(input, session);
            return true;
        }
        completed = null;
        return false;
    }

    private static InputRegistryState CreateInput(
        InputRegistryState state,
        RuntimeEventEnvelope runtimeEvent,
        DateTimeOffset occurredAt)
    {
        var requestId = PayloadString(runtimeEvent.Payload, "requestId");
        if (state.Requests.TryGetValue(requestId, out var existing))
        {
            if (existing.SessionId == runtimeEvent.Session!.ExternalId &&
                existing.Status == InputRequestStatuses.Pending)
            {
                return state;
            }
            throw new InvalidOperationException($"补充问题 {requestId} 已存在且语义冲突。 ");
        }
        var questions = runtimeEvent.Payload.GetProperty("questions")
            .EnumerateArray()
            .Select(question => new InputQuestionState(
                PayloadString(question, "id"),
                question.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean(),
                !question.TryGetProperty("allowsCustom", out var custom) || custom.GetBoolean(),
                question.TryGetProperty("options", out var options)
                    ? options.EnumerateArray().Select(item => item.GetString()!).ToArray()
                    : [],
                OptionalPayloadString(question, "header"),
                OptionalPayloadString(question, "prompt") ?? PayloadString(question, "id"),
                question.TryGetProperty("isSecret", out var secret) && secret.GetBoolean()))
            .ToArray();
        return InputStateMachine.Create(
            state,
            new InputRequestState(
                requestId,
                runtimeEvent.Session!.ExternalId,
                InputRequestStatuses.Pending,
                occurredAt,
                PayloadTimestamp(runtimeEvent.Payload, "expiresAt"),
                questions,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)));
    }

    private static InputRegistryState ResolveSessionInputs(
        InputRegistryState state,
        string sessionId,
        DateTimeOffset occurredAt)
    {
        foreach (var input in state.Requests.Values.Where(item =>
                     item.SessionId == sessionId && item.Status == InputRequestStatuses.Pending).ToArray())
        {
            state = InputStateMachine.ResolveExternally(
                state,
                input.RequestId,
                occurredAt).State;
        }
        return state;
    }

    private static void EnsureInputSession(
        InputRegistryState state,
        string requestId,
        string sessionId)
    {
        if (!state.Requests.TryGetValue(requestId, out var input))
        {
            throw new KeyNotFoundException($"补充问题 {requestId} 尚未登记。 ");
        }
        if (!string.Equals(input.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"补充问题 {requestId} 不属于会话 {sessionId}。 ");
        }
    }
}
