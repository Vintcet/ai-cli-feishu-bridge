using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

public sealed record BridgeBusinessStateSnapshot(
    bool Initialized,
    string SourceStatus,
    long Revision,
    long RejectedFeishuIntents,
    SessionDirectoryState Sessions,
    ApprovalRegistryState Approvals,
    InputRegistryState Inputs)
{
    public static BridgeBusinessStateSnapshot NotInitialized { get; } = new(
        false,
        BridgeStoreShadowStatuses.NotLoaded,
        0,
        0,
        SessionDirectoryState.Empty,
        ApprovalRegistryState.Empty,
        InputRegistryState.Empty);
}

public sealed class BridgeBusinessStateOwner(IBridgeStoreShadow storeShadow)
    : IBridgeRuntimeEventHandler,
      IBridgeFeishuIntentHandler,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth
{
    private const string PassiveIntentMessage =
        "C# Shadow 当前只读观测，未执行这次飞书操作。";
    private readonly object sync = new();
    private BridgeBusinessStateSnapshot snapshot = BridgeBusinessStateSnapshot.NotInitialized;

    public string Name => "business-state-owner";

    public BridgeBusinessStateSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return snapshot;
            }
        }
    }

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = Snapshot;
            return current.Initialized
                ? new(
                    Name,
                    "passive",
                    $"shadow sessions={current.Sessions.Sessions.Count} " +
                    $"approvals={current.Approvals.Requests.Count} " +
                    $"inputs={current.Inputs.Requests.Count}")
                : new(Name, "failed", $"source={current.SourceStatus}");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = storeShadow.Snapshot;
        lock (sync)
        {
            snapshot = source.Core is null
                ? BridgeBusinessStateSnapshot.NotInitialized with
                {
                    SourceStatus = source.Status,
                }
                : new(
                    true,
                    source.Status,
                    0,
                    0,
                    source.Core.Sessions,
                    source.Core.Approvals,
                    InputRegistryState.Empty);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task HandleAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        lock (sync)
        {
            var current = RequireInitialized();
            var occurredAt = DateTimeOffset.Parse(runtimeEvent.OccurredAt);
            var sessions = current.Sessions;
            var approvals = current.Approvals;
            var inputs = current.Inputs;
            var sessionId = runtimeEvent.Session!.ExternalId;

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
                    EnsureSession(sessions, runtimeEvent);
                    approvals = CreateApproval(approvals, runtimeEvent, occurredAt);
                    sessions = TransitionSession(
                        sessions,
                        runtimeEvent,
                        SessionStatuses.PendingApproval,
                        occurredAt);
                    break;
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
                        var transition = InputStateMachine.ResolveExternally(
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

            if (ReferenceEquals(sessions, current.Sessions) &&
                ReferenceEquals(approvals, current.Approvals) &&
                ReferenceEquals(inputs, current.Inputs))
            {
                return Task.CompletedTask;
            }
            snapshot = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Approvals = approvals,
                Inputs = inputs,
            };
        }
        return Task.CompletedTask;
    }

    public Task<FeishuCallbackResult?> HandleAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(intent);
        lock (sync)
        {
            var current = RequireInitialized();
            snapshot = current with
            {
                RejectedFeishuIntents = current.RejectedFeishuIntents + 1,
            };
        }
        return Task.FromResult<FeishuCallbackResult?>(
            new("warning", PassiveIntentMessage));
    }

    private BridgeBusinessStateSnapshot RequireInitialized() =>
        snapshot.Initialized
            ? snapshot
            : throw new InvalidOperationException(
                $"业务状态所有者尚未从 Node Store 初始化：{snapshot.SourceStatus}。");

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

    private static ApprovalRegistryState CreateApproval(
        ApprovalRegistryState state,
        RuntimeEventEnvelope runtimeEvent,
        DateTimeOffset occurredAt)
    {
        var requestId = PayloadString(runtimeEvent.Payload, "requestId");
        if (state.Requests.TryGetValue(requestId, out var existing))
        {
            if (existing.SessionId == runtimeEvent.Session!.ExternalId &&
                existing.Status == ApprovalStatuses.Pending)
            {
                return state;
            }
            throw new InvalidOperationException($"审批 {requestId} 已存在且语义冲突。 ");
        }
        return ApprovalStateMachine.Create(
            state,
            new ApprovalState(
                requestId,
                runtimeEvent.Session!.ExternalId,
                ApprovalStatuses.Pending,
                occurredAt,
                PayloadTimestamp(runtimeEvent.Payload, "expiresAt"),
                []));
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
                    : []))
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

    private static ApprovalRegistryState ResolveSessionApprovals(
        ApprovalRegistryState state,
        string sessionId,
        DateTimeOffset occurredAt)
    {
        foreach (var approval in state.Requests.Values.Where(item =>
                     item.SessionId == sessionId && item.Status == ApprovalStatuses.Pending).ToArray())
        {
            state = ApprovalStateMachine.ResolveExternally(
                state,
                approval.RequestId,
                ApprovalResolutions.Local,
                occurredAt).State;
        }
        return state;
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

    private static void EnsureApprovalSession(
        ApprovalRegistryState state,
        string requestId,
        string sessionId)
    {
        if (!state.Requests.TryGetValue(requestId, out var approval))
        {
            throw new KeyNotFoundException($"审批 {requestId} 尚未登记。 ");
        }
        if (!string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"审批 {requestId} 不属于会话 {sessionId}。 ");
        }
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

    private static string ApprovalResolution(JsonElement payload) =>
        PayloadString(payload, "outcome") switch
        {
            "allowed" => ApprovalResolutions.Allow,
            "denied" => ApprovalResolutions.Deny,
            "cancelled" => ApprovalResolutions.Local,
            var value => throw new InvalidDataException($"不支持的外部审批结果 {value}。"),
        };

    private static string ExternalApprovalSessionStatus(JsonElement payload) =>
        PayloadString(payload, "outcome") == "allowed"
            ? SessionStatuses.Running
            : SessionStatuses.Waiting;

    private static string PayloadString(JsonElement payload, string name) =>
        payload.GetProperty(name).GetString()!;

    private static DateTimeOffset PayloadTimestamp(JsonElement payload, string name) =>
        DateTimeOffset.Parse(PayloadString(payload, name));
}
