using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveRuntimeActivityCoordinator
{
    public async Task RecordAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        EnsureStarted();
        if (!IsActivityEvent(runtimeEvent))
        {
            return;
        }

        BridgeStoreSnapshot store;
        try
        {
            store = await storeOwner.ReadAsync(cancellationToken);
        }
        catch
        {
            // A progress card must never turn a successful Hook/SSE delivery
            // into a runtime failure when the Store is temporarily unavailable.
            return;
        }
        if (store.Settings.NotifyActivity != true ||
            !store.Sessions.Sessions.TryGetValue(
                runtimeEvent.Session!.ExternalId,
                out var session) ||
            session.Status == SessionStatuses.Ended)
        {
            return;
        }

        var activity = ToActivityEvent(runtimeEvent);
        var turnId = OptionalString(runtimeEvent.Payload, "turnId") ??
            runtimeEvent.CorrelationId;
        ActivityState? previous = null;
        ActivityState state;
        var created = false;
        lock (sync)
        {
            if (states.TryGetValue(session.SessionId, out state!))
            {
                if (state.Completed ||
                    state.TurnId is not null && turnId is not null &&
                    !string.Equals(state.TurnId, turnId, StringComparison.Ordinal))
                {
                    previous = state;
                    states.Remove(session.SessionId);
                    state = null!;
                }
            }
            if (state is null)
            {
                var marker = ReadActivityMarker(session);
                var markerMatches = marker is not null &&
                    (turnId is null || marker.TurnId is null ||
                     string.Equals(marker.TurnId, turnId, StringComparison.Ordinal));
                var activityKey = markerMatches
                    ? marker!.Key
                    : ActivityKey(session.SessionId, turnId, runtimeEvent.EventId);
                state = new(
                    session.SessionId,
                    activityKey,
                    turnId,
                    markerMatches ? marker!.StartedAt : runtimeEvent.OccurredAt,
                    markerMatches ? marker!.Status == "completed" : false);
                if (markerMatches)
                {
                    RestoreRoutes(state, store, session.SessionId);
                }
                states.Add(session.SessionId, state);
                created = true;
            }
            state.TurnId ??= turnId;
            state.Events.Add(activity);
            if (state.Events.Count > MaximumEvents)
            {
                state.Events.RemoveRange(0, state.Events.Count - MaximumEvents);
            }
            state.Revision++;
            state.Completed = false;
        }

        if (previous is not null)
        {
            await CompleteDetachedAsync(
                previous,
                "上一轮已结束",
                cancellationToken);
        }
        if (created)
        {
            await PersistMarkerAsync(state, cancellationToken);
        }
        ScheduleFlush(state);
    }

    public async Task FinishAsync(
        string sessionId,
        string label,
        string? turnId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        EnsureStarted();

        BridgeStoreSnapshot store;
        try
        {
            store = await storeOwner.ReadAsync(cancellationToken);
        }
        catch
        {
            return;
        }

        ActivityState? state;
        lock (sync)
        {
            states.TryGetValue(sessionId, out state);
        }
        if (state is null &&
            store.Sessions.Sessions.TryGetValue(sessionId, out var storedSession))
        {
            state = Rehydrate(storedSession, store);
            if (state is not null)
            {
                lock (sync)
                {
                    if (!states.ContainsKey(sessionId))
                    {
                        states[sessionId] = state;
                    }
                    else
                    {
                        state = states[sessionId];
                    }
                }
            }
        }
        if (state is null)
        {
            return;
        }

        lock (sync)
        {
            if (state.TurnId is not null && turnId is not null &&
                !string.Equals(state.TurnId, turnId, StringComparison.Ordinal))
            {
                return;
            }
            if (!state.Completed)
            {
                state.Completed = true;
                state.Events.Add(new(
                    clock.GetUtcNow().ToString("O"),
                    label));
                if (state.Events.Count > MaximumEvents)
                {
                    state.Events.RemoveRange(0, state.Events.Count - MaximumEvents);
                }
                state.Revision++;
            }
        }
        await FlushStateAsync(state, force: true, cancellationToken);
    }

}
