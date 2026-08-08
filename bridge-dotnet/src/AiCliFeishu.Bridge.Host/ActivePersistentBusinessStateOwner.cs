using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActivePersistentBusinessStateOwner(
    BridgeHostOptions options,
    IBridgeProductionStoreOwner storeOwner,
    TimeProvider? timeProvider = null)
    : IBridgePersistentBusinessStateOwner,
      IBridgeControlBusinessStateSource,
      IBridgeActiveRuntimeStateSink,
      IBridgeActiveSessionAliasStateOwner,
      IBridgeActiveApprovalStateOwner,
      IBridgeActiveInputStateOwner,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly HashSet<string> inputClaims = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private BridgeBusinessStateSnapshot snapshot =
        BridgeBusinessStateSnapshot.NotInitialized;

    public string Name => "persistent-business-state-owner";

    public BridgeBusinessStateSnapshot Snapshot => Volatile.Read(ref snapshot);

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = Snapshot;
            return current.Initialized
                ? new(
                    Name,
                    "ready",
                    $"persistent sessions={current.Sessions.Sessions.Count} " +
                    $"approvals={current.Approvals.Requests.Count} " +
                    $"inputs={current.Inputs.Requests.Count}")
                : new(Name, "failed", $"source={current.SourceStatus}");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.Initialized)
            {
                return;
            }
            var store = await storeOwner.ReadAsync(cancellationToken);
            var core = NodeStoreCoreProjection.Project(store);
            var observedAt = clock.GetUtcNow();
            var recovered = ApprovalStateMachine.RecoverPending(
                core.Approvals,
                observedAt);
            var sessions = RecoverApprovalSessions(
                core.Sessions,
                core.Approvals,
                observedAt);
            var initialized = new BridgeBusinessStateSnapshot(
                true,
                "production",
                0,
                0,
                sessions,
                recovered.State,
                InputRegistryState.Empty);

            var sessionsChanged = !ReferenceEquals(sessions, core.Sessions);
            if (recovered.Value > 0 || sessionsChanged)
            {
                await PersistAsync(initialized, cancellationToken);
            }
            Volatile.Write(ref snapshot, initialized);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Active business state is the authoritative in-memory projection. It is
        // advanced only after the production Store write succeeds; a control API
        // refresh must not re-read the files and overwrite newer runtime state.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async ValueTask<BridgeSessionAliasUpdateResult> UpdateSessionAliasAsync(
        string sessionId,
        string? alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string? normalizedAlias = null;
        if (alias is not null)
        {
            var validationError = SessionAliasRules.ValidationError(alias);
            if (validationError is not null)
            {
                return new(null, null, validationError);
            }
            normalizedAlias = SessionAliasRules.Normalize(alias);
        }

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            _ = RequireInitialized();
            var observed = await storeOwner.ReadAsync(cancellationToken);
            var rejection = AliasUpdateRejection(
                observed,
                sessionId,
                normalizedAlias);
            if (rejection is not null)
            {
                return rejection;
            }

            BridgeSessionAliasUpdateResult? result = null;
            await storeOwner.UpdateAsync(
                store =>
                {
                    var currentRejection = AliasUpdateRejection(
                        store,
                        sessionId,
                        normalizedAlias);
                    if (currentRejection is not null)
                    {
                        result = currentRejection;
                        return store;
                    }

                    var updated = NodeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        sessionId,
                        new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                        {
                            ["alias"] = normalizedAlias is null
                                ? null
                                : JsonSerializer.SerializeToElement(normalizedAlias),
                        });
                    result = new(
                        updated.Sessions.Sessions[sessionId],
                        null,
                        null);
                    return updated;
                },
                cancellationToken);
            return result ?? throw new InvalidOperationException(
                "会话别名更新没有产生结果。 ");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeApprovalClaim?> TryClaimApprovalAsync(
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
            if (!current.Approvals.Requests.TryGetValue(requestId, out var approval) ||
                approval.Status != ApprovalStatuses.Pending ||
                !string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) ||
                !current.Sessions.Sessions.TryGetValue(sessionId, out var session))
            {
                return null;
            }
            var claim = ApprovalStateMachine.Claim(current.Approvals, requestId);
            if (!claim.Value)
            {
                return null;
            }
            Volatile.Write(ref snapshot, current with { Approvals = claim.State });
            return new(approval, session);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask ReleaseApprovalClaimAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            var approvals = ApprovalStateMachine.ReleaseClaim(
                current.Approvals,
                requestId);
            if (!ReferenceEquals(approvals, current.Approvals))
            {
                Volatile.Write(ref snapshot, current with { Approvals = approvals });
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeApprovalClaim?> ResolveClaimedApprovalAsync(
        string requestId,
        string sessionId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (resolution is not ApprovalResolutions.Allow and not ApprovalResolutions.Deny)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = RequireInitialized();
            if (!TryClaimedApproval(
                    current,
                    requestId,
                    sessionId,
                    out var approval,
                    out var session))
            {
                return null;
            }
            var resolvedAt = Latest(
                clock.GetUtcNow(),
                approval.CreatedAt,
                session.LastSeenAt);
            var resolved = ApprovalStateMachine.ResolveClaimed(
                current.Approvals,
                requestId,
                resolution,
                resolvedAt);
            if (!resolved.Value)
            {
                Volatile.Write(ref snapshot, current with { Approvals = resolved.State });
                return null;
            }
            var sessions = SessionStateMachine.Transition(
                current.Sessions,
                sessionId,
                resolution == ApprovalResolutions.Allow
                    ? SessionStatuses.Running
                    : SessionStatuses.Waiting,
                resolvedAt);
            var next = current with
            {
                Revision = current.Revision + 1,
                Sessions = sessions,
                Approvals = resolved.State,
            };
            await PersistAsync(next, cancellationToken);
            Volatile.Write(ref snapshot, next);
            return new(
                next.Approvals.Requests[requestId],
                next.Sessions.Sessions[sessionId]);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<BridgeApprovalClaim?> DeferClaimedApprovalAsync(
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
            if (!TryClaimedApproval(
                    current,
                    requestId,
                    sessionId,
                    out var approval,
                    out var session))
            {
                return null;
            }
            var approvals = ApprovalStateMachine.ReleaseClaim(
                current.Approvals,
                requestId);
            var next = current with
            {
                Revision = current.Revision + 1,
                Approvals = approvals,
            };
            var patch = new NodeStoreApprovalExtensionPatch(
                requestId,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["desktopApprovalRequested"] =
                        JsonSerializer.SerializeToElement(true),
                });
            await PersistAsync(
                next,
                cancellationToken,
                approvalExtensionPatch: patch);
            Volatile.Write(ref snapshot, next);
            return new(approval, session);
        }
        finally
        {
            writeGate.Release();
        }
    }

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
            Volatile.Write(ref snapshot, current with
            {
                Revision = current.Revision + 1,
                Inputs = recorded.State,
            });
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
            inputClaims.Remove(requestId);
            if (reset.Value)
            {
                Volatile.Write(ref snapshot, current with
                {
                    Revision = current.Revision + 1,
                    Inputs = reset.State,
                });
                input = reset.State.Requests[requestId];
            }
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
            await PersistAsync(next, cancellationToken, sessionExtensionPatch);
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

    private async Task PersistAsync(
        BridgeBusinessStateSnapshot business,
        CancellationToken cancellationToken,
        NodeStoreSessionExtensionPatch? sessionExtensionPatch = null,
        NodeStoreApprovalExtensionPatch? approvalExtensionPatch = null)
    {
        await storeOwner.UpdateAsync(
            store => NodeStoreBusinessStateMerger.Merge(
                store,
                business.Sessions,
                business.Approvals,
                sessionExtensionPatch,
                approvalExtensionPatch),
            cancellationToken);
    }

    private static bool TryClaimedApproval(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out ApprovalState approval,
        out SessionState session)
    {
        if (current.Approvals.Claims.Contains(requestId) &&
            current.Approvals.Requests.TryGetValue(requestId, out approval!) &&
            approval.Status == ApprovalStatuses.Pending &&
            string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        approval = null!;
        session = null!;
        return false;
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

    private static DateTimeOffset Latest(params DateTimeOffset[] values) =>
        values.Max();

    private static bool IsAliasReserved(SessionStoreRecord session)
    {
        if (!string.Equals(
                session.Status,
                SessionStatuses.Ended,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (session.SessionId.StartsWith(
                "managed-terminal-",
                StringComparison.Ordinal))
        {
            return false;
        }
        return ExtensionBoolean(session, "historyEligible") &&
            !HasNonEmptyExtension(session, "historyHiddenAt");
    }

    private static BridgeSessionAliasUpdateResult? AliasUpdateRejection(
        NodeStoreSnapshot store,
        string sessionId,
        string? normalizedAlias)
    {
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !IsAliasReserved(session))
        {
            return new(null, null, "会话不存在或已经失效。");
        }
        if (normalizedAlias is null)
        {
            return null;
        }

        var aliasKey = SessionAliasRules.Key(normalizedAlias);
        var conflict = store.Sessions.Sessions.Values
            .Where(IsAliasReserved)
            .FirstOrDefault(candidate =>
                !string.Equals(
                    candidate.SessionId,
                    sessionId,
                    StringComparison.Ordinal) &&
                ExtensionString(candidate, "alias") is { } currentAlias &&
                SessionAliasRules.Key(currentAlias) == aliasKey);
        return conflict is null ? null : new(null, conflict, null);
    }

    private static bool HasNonEmptyExtension(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.Any(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase) &&
            item.Value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(item.Value.GetString()));

    private static bool ExtensionBoolean(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.Any(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase) &&
            item.Value.ValueKind is JsonValueKind.True);

    private static string? ExtensionString(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            is { Value.ValueKind: JsonValueKind.String } item &&
        !string.IsNullOrWhiteSpace(item.Value.GetString())
            ? item.Value.GetString()!.Trim()
            : null;

    private static NodeStoreSessionExtensionPatch? SessionExtensionPatch(
        RuntimeEventEnvelope runtimeEvent)
    {
        if (runtimeEvent.EventType is not RuntimeEventTypes.SessionStarted ||
            runtimeEvent.Payload.ValueKind is not JsonValueKind.Object ||
            !runtimeEvent.Payload.TryGetProperty(
                "managedTerminalId",
                out var terminalIdElement))
        {
            return null;
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
        return new(runtimeEvent.Session!.ExternalId, values);
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "持久化业务状态 Owner 只能用于 Active Host。");
        }
    }

    private static SessionDirectoryState RecoverApprovalSessions(
        SessionDirectoryState sessions,
        ApprovalRegistryState loadedApprovals,
        DateTimeOffset observedAt)
    {
        foreach (var loaded in loadedApprovals.Requests.Values.Where(item =>
                     item.Status == ApprovalStatuses.Pending))
        {
            if (!sessions.Sessions.TryGetValue(loaded.SessionId, out var session) ||
                session.Status != SessionStatuses.PendingApproval)
            {
                continue;
            }
            var occurredAt = observedAt >= session.LastSeenAt
                ? observedAt
                : session.LastSeenAt;
            sessions = SessionStateMachine.Transition(
                sessions,
                session.SessionId,
                SessionStatuses.LocalApproval,
                occurredAt);
        }
        return sessions;
    }

    private BridgeBusinessStateSnapshot RequireInitialized() =>
        Snapshot.Initialized
            ? Snapshot
            : throw new InvalidOperationException(
                $"业务状态所有者尚未从生产 Store 初始化：{Snapshot.SourceStatus}。");

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
        SessionDirectoryState sessions,
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
                [],
                TurnId: runtimeEvent.CorrelationId ?? requestId,
                Cwd: sessions.Sessions[runtimeEvent.Session.ExternalId].Cwd,
                ToolName: PayloadString(runtimeEvent.Payload, "title"),
                ToolPreview: OptionalPayloadString(
                    runtimeEvent.Payload,
                    "description") ?? string.Empty));
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
