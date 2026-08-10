using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActivePersistentBusinessStateOwner
{
    public async Task HandleAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            var occurredAt = DateTimeOffset.Parse(runtimeEvent.OccurredAt);
            var sessions = current.Sessions;
            var approvals = current.Approvals;
            var inputs = current.Inputs;
            var sessionId = runtimeEvent.Session!.ExternalId;
            BridgeStoreApprovalExtensionPatch? approvalExtensionPatch = null;

            switch (runtimeEvent.EventType)
            {
                case RuntimeEventTypes.SessionStarted:
                    sessions = StartSession(sessions, runtimeEvent, occurredAt);
                    break;
                case RuntimeEventTypes.SessionEnded:
                case RuntimeEventTypes.RuntimeDisconnected:
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.Ended,
                        occurredAt);
                    approvals = ResolveSessionApprovals(approvals, sessionId, occurredAt);
                    inputs = ResolveSessionInputs(inputs, sessionId, occurredAt);
                    break;
                case RuntimeEventTypes.RuntimeConnected:
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.Ready,
                        occurredAt,
                        allowCreate: true);
                    break;
                case RuntimeEventTypes.TurnStarted:
                case RuntimeEventTypes.TurnActivity:
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.Running,
                        occurredAt);
                    break;
                case RuntimeEventTypes.TurnCompleted:
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.Waiting,
                        occurredAt);
                    break;
                case RuntimeEventTypes.TurnFailed:
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.Error,
                        occurredAt,
                        PayloadString(runtimeEvent.Payload, "error"));
                    break;
                case RuntimeEventTypes.ApprovalRequested:
                    {
                        EnsureSession(sessions, runtimeEvent);
                        approvals = CreateApproval(
                            approvals,
                            sessions,
                            runtimeEvent,
                            occurredAt);
                        sessions = TransitionSession(
                            sessions,
                            runtimeEvent,
                            SessionStatuses.PendingApproval,
                            occurredAt);
                        var requestId = PayloadString(runtimeEvent.Payload, "requestId");
                        var created = approvals.Requests[requestId];
                        var risk = ApprovalRiskClassifier.Assess(
                            created.ToolName,
                            created.ToolPreview,
                            created.Cwd);
                        approvalExtensionPatch = new(
                            requestId,
                            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                            {
                                ["riskLevel"] = JsonSerializer.SerializeToElement(risk.Level),
                                ["riskReason"] = JsonSerializer.SerializeToElement(risk.Reason),
                            });
                        break;
                    }
                case RuntimeEventTypes.ApprovalResolvedExternally:
                    {
                        EnsureSession(sessions, runtimeEvent);
                        var requestId = PayloadString(runtimeEvent.Payload, "requestId");
                        EnsureApprovalSession(approvals, requestId, sessionId);
                        var transition = ApprovalStateMachine.ResolveExternally(
                            approvals,
                            requestId,
                            ApprovalResolution(runtimeEvent.Payload),
                            occurredAt);
                        if (!transition.Value)
                        {
                            break;
                        }
                        approvals = transition.State;
                        sessions = TransitionSession(
                            sessions,
                            runtimeEvent,
                            ExternalApprovalSessionStatus(runtimeEvent.Payload),
                            occurredAt);
                        break;
                    }
                case RuntimeEventTypes.InputRequested:
                    EnsureSession(sessions, runtimeEvent);
                    inputs = CreateInput(inputs, runtimeEvent, occurredAt);
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.PendingInput,
                        occurredAt);
                    break;
                case RuntimeEventTypes.InputResolvedExternally:
                    {
                        EnsureSession(sessions, runtimeEvent);
                        var requestId = PayloadString(runtimeEvent.Payload, "requestId");
                        EnsureInputSession(inputs, requestId, sessionId);
                        var pending = inputs.Requests[requestId];
                        var transition = inputClaims.Contains(requestId) &&
                            InputStateMachine.HasCompleteAnswers(pending)
                                ? InputStateMachine.Answer(
                                    inputs,
                                    requestId,
                                    pending.Answers,
                                    occurredAt)
                                : InputStateMachine.ResolveExternally(
                                    inputs,
                                    requestId,
                                    occurredAt);
                        if (!transition.Value)
                        {
                            break;
                        }
                        inputs = transition.State;
                        sessions = TransitionSession(
                            sessions,
                            runtimeEvent,
                            SessionStatuses.Running,
                            occurredAt);
                        break;
                    }
                default:
                    throw new InvalidDataException(
                        $"状态所有者不支持 Runtime 事件 {runtimeEvent.EventType}。");
            }

            var sessionExtensionPatch = SessionExtensionPatch(runtimeEvent);
            sessions = BridgeStoreRetention.PruneEndedSessions(sessions, occurredAt);
            approvals = ApprovalRetention.Prune(
                approvals,
                occurredAt,
                RetentionPolicy.Default);
            if (ReferenceEquals(sessions, current.Sessions) &&
                ReferenceEquals(approvals, current.Approvals) &&
                ReferenceEquals(inputs, current.Inputs) &&
                sessionExtensionPatch is null)
            {
                return;
            }
            var next = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Approvals = approvals,
                Inputs = inputs,
            };
            await PersistAsync(
                next,
                cancellationToken,
                sessionExtensionPatch,
                approvalExtensionPatch);
            Volatile.Write(ref snapshot, next);
            inputClaims.RemoveWhere(requestId =>
                !next.Inputs.Requests.TryGetValue(requestId, out var input) ||
                input.Status != InputRequestStatuses.Pending);
        }
        finally
        {
            writeGate.Release();
        }
    }


    private static BridgeStoreSessionExtensionPatch? SessionExtensionPatch(
        RuntimeEventEnvelope runtimeEvent)
    {
        if (runtimeEvent.EventType is not RuntimeEventTypes.SessionStarted ||
            runtimeEvent.Payload.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }
        var openCode = string.Equals(
            runtimeEvent.Runtime,
            RuntimeNames.OpenCode,
            StringComparison.Ordinal);
        if (!runtimeEvent.Payload.TryGetProperty(
                "managedTerminalId",
                out var terminalIdElement))
        {
            var transcriptValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (openCode)
            {
                transcriptValues["managedByAssistant"] = JsonSerializer.SerializeToElement(true);
                transcriptValues["historyEligible"] = JsonSerializer.SerializeToElement(true);
                transcriptValues["source"] = JsonSerializer.SerializeToElement("opencode");
            }
            if (runtimeEvent.Payload.TryGetProperty("transcriptPath", out var transcriptElement) &&
                transcriptElement.ValueKind is JsonValueKind.String &&
                transcriptElement.GetString() is { Length: > 0 } transcriptFilePath &&
                Path.IsPathFullyQualified(transcriptFilePath) &&
                string.Equals(
                    Path.GetExtension(transcriptFilePath),
                    ".jsonl",
                    StringComparison.OrdinalIgnoreCase))
            {
                transcriptValues["transcriptPath"] = JsonSerializer.SerializeToElement(
                    Path.GetFullPath(transcriptFilePath));
            }
            return transcriptValues.Count == 0
                ? null
                : new(runtimeEvent.Session!.ExternalId, transcriptValues);
        }
        if (terminalIdElement.ValueKind is not JsonValueKind.String ||
            terminalIdElement.GetString() is not { } terminalId ||
            terminalId.Length is < 8 or > 64 ||
            terminalId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidDataException("标准会话事件包含无效的托管终端 ID。");
        }
        if (!runtimeEvent.Payload.TryGetProperty(
                "managedTerminalElevated",
                out var elevatedElement) ||
            elevatedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException("标准会话事件缺少托管终端权限身份。");
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["managedTerminalId"] = JsonSerializer.SerializeToElement(terminalId),
            ["managedTerminalElevated"] = elevatedElement.Clone(),
            ["managedByAssistant"] = JsonSerializer.SerializeToElement(true),
            ["historyEligible"] = JsonSerializer.SerializeToElement(true),
        };
        if (runtimeEvent.Payload.TryGetProperty("source", out var sourceElement) &&
            sourceElement.ValueKind is JsonValueKind.String &&
            sourceElement.GetString() is { Length: > 0 } source)
        {
            values["source"] = JsonSerializer.SerializeToElement(source);
        }
        else if (openCode)
        {
            values["source"] = JsonSerializer.SerializeToElement("opencode");
        }
        if (runtimeEvent.Payload.TryGetProperty("transcriptPath", out var transcript) &&
            transcript.ValueKind is JsonValueKind.String &&
            transcript.GetString() is { Length: > 0 } transcriptPath &&
            Path.IsPathFullyQualified(transcriptPath) &&
            string.Equals(
                Path.GetExtension(transcriptPath),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase))
        {
            values["transcriptPath"] = JsonSerializer.SerializeToElement(
                Path.GetFullPath(transcriptPath));
        }
        return new(runtimeEvent.Session!.ExternalId, values);
    }

    private static SessionDirectoryState StartSession(
        SessionDirectoryState state,
        RuntimeEventEnvelope runtimeEvent,
        DateTimeOffset occurredAt)
    {
        var sessionId = runtimeEvent.Session!.ExternalId;
        if (!state.Sessions.TryGetValue(sessionId, out var current))
        {
            var cwd = RequireCwd(runtimeEvent);
            var registered = SessionStateMachine.Register(
                state,
                new SessionState(
                    sessionId,
                    runtimeEvent.Runtime,
                    cwd,
                    SessionStatuses.Starting,
                    occurredAt,
                    occurredAt));
            return SessionStateMachine.Transition(
                registered,
                sessionId,
                SessionStatuses.Ready,
                occurredAt);
        }
        EnsureRuntime(current, runtimeEvent.Runtime);
        var started = current.Status == SessionStatuses.Ended
            ? SessionStateMachine.Transition(
                state,
                sessionId,
                SessionStatuses.Starting,
                occurredAt)
            : state;
        return SessionStateMachine.Transition(
            started,
            sessionId,
            SessionStatuses.Ready,
            occurredAt);
    }

    private static SessionDirectoryState TransitionSession(
        SessionDirectoryState state,
        RuntimeEventEnvelope runtimeEvent,
        string status,
        DateTimeOffset occurredAt,
        string? error = null,
        bool allowCreate = false)
    {
        var sessionId = runtimeEvent.Session!.ExternalId;
        if (!state.Sessions.TryGetValue(sessionId, out var current))
        {
            if (!allowCreate)
            {
                throw new KeyNotFoundException($"会话 {sessionId} 尚未登记。 ");
            }
            state = SessionStateMachine.Register(
                state,
                new SessionState(
                    sessionId,
                    runtimeEvent.Runtime,
                    RequireCwd(runtimeEvent),
                    SessionStatuses.Starting,
                    occurredAt,
                    occurredAt));
        }
        else
        {
            EnsureRuntime(current, runtimeEvent.Runtime);
            if (allowCreate && current.Status == SessionStatuses.Ended)
            {
                state = SessionStateMachine.Transition(
                    state,
                    sessionId,
                    SessionStatuses.Starting,
                    occurredAt);
            }
        }
        return SessionStateMachine.Transition(state, sessionId, status, occurredAt, error);
    }

    private static void EnsureSession(
        SessionDirectoryState state,
        RuntimeEventEnvelope runtimeEvent)
    {
        if (!state.Sessions.TryGetValue(runtimeEvent.Session!.ExternalId, out var current))
        {
            throw new KeyNotFoundException(
                $"会话 {runtimeEvent.Session.ExternalId} 尚未登记。 ");
        }
        EnsureRuntime(current, runtimeEvent.Runtime);
    }

    private static void EnsureRuntime(SessionState session, string runtime)
    {
        if (!string.Equals(session.Runtime, runtime, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"会话 {session.SessionId} 已属于 {session.Runtime}，不能接收 {runtime} 事件。 ");
        }
    }

    private static string RequireCwd(RuntimeEventEnvelope runtimeEvent) =>
        runtimeEvent.Session!.Cwd is { Length: > 0 } cwd
            ? cwd
            : throw new InvalidDataException(
                $"新会话 {runtimeEvent.Session.ExternalId} 缺少工作目录。");


    private static string? OptionalPayloadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string PayloadString(JsonElement payload, string name) =>
        payload.GetProperty(name).GetString()!;

    private static DateTimeOffset PayloadTimestamp(JsonElement payload, string name) =>
        DateTimeOffset.Parse(PayloadString(payload, name));
}
