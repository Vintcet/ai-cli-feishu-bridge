using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveRuntimeRetryCoordinator
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }
            started = true;
        }

        if (activity is not null)
        {
            await activity.StartAsync(cancellationToken);
        }

        var store = await storeOwner.ReadAsync(cancellationToken);
        await RestoreTranscriptWatchesAsync(store, cancellationToken);
        RestoreRetryStates(store);
        foreach (var session in store.Sessions.Sessions.Values)
        {
            if (!string.Equals(
                    ExtensionString(session, "lastNotificationStatus"),
                    "pending",
                    StringComparison.Ordinal) ||
                ExtensionString(session, "lastNotificationTurnId") is not { } turnId)
            {
                continue;
            }
            var pendingKind = ExtensionString(session, "pendingNotificationKind") ??
                (string.Equals(
                    session.Status,
                    SessionStatuses.Error,
                    StringComparison.Ordinal)
                    ? BridgeRuntimeNotificationKinds.Error
                    : BridgeRuntimeNotificationKinds.Stop);
            try
            {
                if (string.Equals(
                        pendingKind,
                        BridgeRuntimeNotificationKinds.Error,
                        StringComparison.Ordinal))
                {
                    if ((ExtensionString(session, "pendingNotificationMessage") ??
                            session.LastError) is not { } error)
                    {
                        continue;
                    }
                    await ProcessFailureAsync(
                        new(
                            Runtime(session),
                            session.SessionId,
                            turnId,
                            error,
                            null,
                            Generation(session.SessionId),
                            $"retry-recovery-{CycleId(session.SessionId, turnId)}",
                            $"retry-recovery-{CycleId(session.SessionId, turnId)}"),
                        cancellationToken);
                }
                else if (string.Equals(
                             pendingKind,
                             BridgeRuntimeNotificationKinds.Stop,
                             StringComparison.Ordinal))
                {
                    await ProcessCompletionNotificationAsync(
                        new(
                            Runtime(session),
                            session.SessionId,
                            turnId,
                            BridgeRuntimeNotificationKinds.Stop,
                            ExtensionString(session, "pendingNotificationMessage") ??
                                ExtensionString(session, "lastAssistantMessage") ??
                                CompletionFallback(session),
                            $"completion-recovery-{CycleId(session.SessionId, turnId)}",
                            $"completion-recovery-{CycleId(session.SessionId, turnId)}"),
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A pending notification remains durable for a later recovery attempt.
            }
        }
        StartRestoredWorkers();
    }

    private void RestoreRetryStates(BridgeStoreSnapshot store)
    {
        var dispatchedPromptSessions = new List<string>();
        lock (sync)
        {
            foreach (var session in store.Sessions.Sessions.Values)
            {
                var persisted = PersistedRetryStateOf(session);
                if (persisted is null ||
                    string.Equals(session.Status, SessionStatuses.Ended, StringComparison.Ordinal) ||
                    !string.Equals(Runtime(session), persisted.Runtime, StringComparison.Ordinal) ||
                    persisted.Attempt <= 0 ||
                    persisted.MaxAttempts <= 0 ||
                    persisted.Attempt > persisted.MaxAttempts ||
                    string.IsNullOrWhiteSpace(persisted.CycleId) ||
                    string.IsNullOrWhiteSpace(persisted.TurnId))
                {
                    continue;
                }

                attemptCounts[session.SessionId] = Math.Max(
                    attemptCounts.GetValueOrDefault(session.SessionId),
                    persisted.Attempt);
                var now = clock.GetUtcNow();
                var dueAt = persisted.Phase == PersistedRetryPhases.Running
                    ? now
                    : persisted.DueAt;
                var delay = dueAt > now ? dueAt - now : TimeSpan.FromMilliseconds(1);
                var cycle = new RetryCycle(
                    persisted.CycleId,
                    $"{persisted.CycleId}:{persisted.Attempt}",
                    persisted.Runtime,
                    session.SessionId,
                    persisted.TurnId,
                    persisted.Error,
                    persisted.Attempt,
                    persisted.MaxAttempts,
                    delay,
                    dueAt,
                    persisted.TraceId,
                    persisted.EventId);
                switch (persisted.Phase)
                {
                    case PersistedRetryPhases.Scheduled:
                    case PersistedRetryPhases.Running:
                        retries = RetryStateMachine.Schedule(
                            retries,
                            new(
                                cycle.TaskId,
                                cycle.SessionId,
                                cycle.Attempt,
                                cycle.MaxAttempts,
                                RetryStatuses.Pending,
                                dueAt,
                                now));
                        cycle.Phase = RetryCyclePhases.Scheduled;
                        break;
                    case PersistedRetryPhases.Dispatched:
                        cycle.Phase = RetryCyclePhases.Running;
                        cycle.WorkerScheduled = true;
                        dispatchedPromptSessions.Add(session.SessionId);
                        break;
                    case PersistedRetryPhases.Stopped:
                        cycle.Phase = RetryCyclePhases.Stopped;
                        cycle.Stopped = true;
                        cycle.WorkerScheduled = true;
                        break;
                    default:
                        continue;
                }
                cycles[session.SessionId] = cycle;
            }
        }
        foreach (var sessionId in dispatchedPromptSessions)
        {
            remotePrompts?.Remember(
                sessionId,
                RetryPrompt,
                BridgeRemotePromptKind.AutomaticRetry);
        }
    }

    private void StartRestoredWorkers()
    {
        lock (sync)
        {
            foreach (var cycle in cycles.Values.Where(cycle =>
                         !cycle.Stopped &&
                         !cycle.WorkerScheduled &&
                         cycle.Phase == RetryCyclePhases.Scheduled))
            {
                cycle.WorkerScheduled = true;
                StartWorker(cycle);
            }
        }
    }

    private async Task RestoreTranscriptWatchesAsync(
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken)
    {
        if (transcriptMonitor is null)
        {
            return;
        }
        foreach (var session in store.Sessions.Sessions.Values)
        {
            if (!string.Equals(Runtime(session), RuntimeNames.Codex, StringComparison.Ordinal) ||
                string.Equals(session.Status, SessionStatuses.Ended, StringComparison.Ordinal) ||
                ExtensionString(session, "transcriptPath") is not { } transcriptPath)
            {
                continue;
            }
            try
            {
                lock (sync)
                {
                    transcriptWatchRestoreAttempts++;
                }
                var watched = await transcriptMonitor.WatchAsync(
                    session.SessionId,
                    transcriptPath,
                    cancellationToken);
                if (watched)
                {
                    lock (sync)
                    {
                        transcriptWatchRestoreSuccesses++;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                lock (sync)
                {
                    transcriptWatchRestoreFailures++;
                }
                // A stale transcript path must not prevent the Host from recovering
                // the remaining active sessions after a restart.
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pendingWorkers;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            started = false;
            lifetime.Cancel();
            pendingWorkers = workers.ToArray();
        }
        try
        {
            await Task.WhenAll(pendingWorkers).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Workers contain their own failure boundaries; shutdown only joins them.
        }
        if (activity is not null)
        {
            try
            {
                await activity.StopAsync(CancellationToken.None);
            }
            catch
            {
                // Activity delivery is best effort and must not block Host shutdown.
            }
        }
        lock (sync)
        {
            cycles.Clear();
            attemptCounts.Clear();
            generations.Clear();
            notificationFlights.Clear();
            retries = RetryRegistryState.Empty;
        }
    }
}
