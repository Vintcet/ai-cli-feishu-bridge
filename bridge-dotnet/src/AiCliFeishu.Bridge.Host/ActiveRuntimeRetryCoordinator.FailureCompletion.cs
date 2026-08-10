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
    public async ValueTask BeginManualTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();
        await ResetAsync(sessionId, cancellationToken);
    }

    public async Task<BridgeRetryStopResult> StopAsync(
        string sessionId,
        string cycleId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();

        RetryCycle? cycle;
        bool retryAlreadyStarted;
        bool alreadyStopped;
        lock (sync)
        {
            if (!cycles.TryGetValue(sessionId, out cycle) ||
                !string.Equals(cycle.CycleId, cycleId, StringComparison.Ordinal))
            {
                return new(BridgeRetryStopKinds.Stale, false);
            }
            retryAlreadyStarted = cycle.Phase == RetryCyclePhases.Running;
            alreadyStopped = cycle.Stopped;
            if (!alreadyStopped)
            {
                cycle.Stopped = true;
                cycle.Phase = RetryCyclePhases.Stopped;
                retries = RetryStateMachine.CancelSession(
                    retries,
                    sessionId,
                    clock.GetUtcNow());
            }
        }

        await PersistRetryStateAsync(
            cycle,
            PersistedRetryPhases.Stopped,
            cancellationToken);

        await PatchCycleCardsAsync(cycle, "stopped", cancellationToken);
        var replacement = await ReplacementCardAsync(cycle, messageId, cancellationToken);
        return new(
            alreadyStopped
                ? BridgeRetryStopKinds.AlreadyStopped
                : BridgeRetryStopKinds.Stopped,
            retryAlreadyStarted,
            replacement);
    }

    private Task ProcessFailureAsync(
        RetryFailure failure,
        CancellationToken cancellationToken) => RunNotificationAsync(
        failure.SessionId,
        failure.TurnId,
        () => ProcessFailureCoreAsync(failure, cancellationToken));

    private async Task ProcessFailureCoreAsync(
        RetryFailure failure,
        CancellationToken cancellationToken)
    {
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(failure.SessionId, out var session) ||
            !string.Equals(Runtime(session), failure.Runtime, StringComparison.Ordinal) ||
            string.Equals(session.Status, SessionStatuses.Ended, StringComparison.Ordinal))
        {
            return;
        }

        var settings = Settings(store.Settings);
        RetryCycle? existing;
        int retryCount;
        long generation;
        lock (sync)
        {
            cycles.TryGetValue(failure.SessionId, out existing);
            attemptCounts.TryGetValue(failure.SessionId, out retryCount);
            generation = GenerationLocked(failure.SessionId);
        }
        var existingForTurn = existing is { Stopped: false } &&
            string.Equals(existing.TurnId, failure.TurnId, StringComparison.Ordinal)
                ? existing
                : null;
        var canRetry = existingForTurn is not null ||
            settings.AutoRetry &&
            generation == failure.Generation &&
            existing?.Stopped != true &&
            retryCount < settings.MaxAttempts &&
            RuntimeErrorClassifier.IsRetryable(failure.Error, failure.ErrorCode) &&
            IsRuntimeReady(session);
        var attempt = existingForTurn?.Attempt ?? retryCount + 1;
        var delay = RetryDelay(settings);
        var notification = new RuntimeNotification(
            failure.Runtime,
            failure.SessionId,
            failure.TurnId,
            BridgeRuntimeNotificationKinds.Error,
            failure.Error,
            failure.TraceId,
            failure.EventId);
        var claim = await TryClaimNotificationAsync(notification, cancellationToken);
        if (claim is null)
        {
            return;
        }
        notification = claim;

        RetryCycle? cycle = null;
        if (canRetry)
        {
            lock (sync)
            {
                if (GenerationLocked(failure.SessionId) != failure.Generation)
                {
                    // A manual turn or terminal lifecycle event won the race
                    // while the durable notification claim was in flight.
                }
                else if (cycles.TryGetValue(failure.SessionId, out var current) &&
                    string.Equals(current.TurnId, failure.TurnId, StringComparison.Ordinal))
                {
                    cycle = current;
                }
                else if (cycles.GetValueOrDefault(failure.SessionId)?.Stopped != true)
                {
                    var cycleId = existing?.CycleId ??
                        CycleId(failure.SessionId, failure.TurnId);
                    var taskId = $"{cycleId}:{attempt}";
                    var now = clock.GetUtcNow();
                    retries = RetryStateMachine.Schedule(
                        retries,
                        new(
                            taskId,
                            failure.SessionId,
                            attempt,
                            settings.MaxAttempts,
                            RetryStatuses.Pending,
                            now + delay,
                            now));
                    cycle = new(
                        cycleId,
                        taskId,
                        failure.Runtime,
                        failure.SessionId,
                        failure.TurnId,
                        notification.Message,
                        attempt,
                        settings.MaxAttempts,
                        delay,
                        now + delay,
                        failure.TraceId,
                        failure.EventId);
                    cycles[failure.SessionId] = cycle;
                }
            }
        }

        if (cycle is not null && existingForTurn is null)
        {
            if (!await PersistRetryStateIfCurrentAsync(
                    cycle,
                    failure.Generation,
                    PersistedRetryPhases.Scheduled,
                    cancellationToken))
            {
                cycle = null;
            }
        }

        var retryView = cycle is null ? null : View(cycle, "scheduled");
        var cards = renderer.RuntimeError(SessionView(session), notification.Message, retryView);
        var chats = await NotificationChatsAsync(
            store,
            session,
            cancellationToken);
        var delivery = await SendCardsAsync(
            notification,
            chats,
            cards,
            cancellationToken);
        await PersistDeliveryAsync(
            notification,
            delivery,
            chats.Count * cards.Count,
            cancellationToken);

        if (cycle is null)
        {
            return;
        }
        lock (sync)
        {
            if (cycles.GetValueOrDefault(cycle.SessionId) != cycle || cycle.Stopped)
            {
                return;
            }
            cycle.Targets = delivery.ToList();
            if (cycle.WorkerScheduled)
            {
                return;
            }
            cycle.WorkerScheduled = true;
            attemptCounts[cycle.SessionId] = cycle.Attempt;
            cycle.Phase = RetryCyclePhases.Scheduled;
            StartWorker(cycle);
        }
    }

    private async Task<bool> ProcessCompletionNotificationAsync(
        RuntimeNotification notification,
        CancellationToken cancellationToken)
    {
        var key = $"{notification.SessionId}\0{notification.TurnId}";
        lock (sync)
        {
            if (!notificationFlights.Add(key))
            {
                return false;
            }
        }

        try
        {
            return await ProcessCompletionNotificationCoreAsync(
                notification,
                cancellationToken);
        }
        finally
        {
            lock (sync)
            {
                notificationFlights.Remove(key);
            }
        }
    }

    private async Task SendCompletedFilesBestEffortAsync(
        string sessionId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (fileTransfers is null)
        {
            return;
        }
        BridgeFileReturnRequest? request;
        try
        {
            request = fileTransfers.AdvanceReturnRequest(sessionId);
        }
        catch
        {
            return;
        }
        if (request is null || paths.Count == 0)
        {
            return;
        }
        try
        {
            await fileTransfers.SendRequestedFilesAsync(
                sessionId,
                request.ChatId,
                paths,
                cancellationToken);
        }
        catch
        {
            // File return is a notification side channel. A failed upload or
            // route write must not turn an already durable Runtime completion
            // into a failed Runtime event.
        }
    }

    private async Task<bool> ProcessCompletionNotificationCoreAsync(
        RuntimeNotification notification,
        CancellationToken cancellationToken)
    {
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(notification.SessionId, out var session) ||
            !string.Equals(Runtime(session), notification.Runtime, StringComparison.Ordinal))
        {
            return false;
        }

        notification = notification with
        {
            Message = string.IsNullOrWhiteSpace(notification.Message)
                ? CompletionFallback(session)
                : notification.Message,
        };
        var claim = await TryClaimNotificationAsync(notification, cancellationToken);
        if (claim is null)
        {
            return false;
        }

        var cards = renderer.RuntimeCompletion(
            SessionView(session),
            claim.Message);
        var chats = await NotificationChatsAsync(
            store,
            session,
            cancellationToken);
        var delivery = await SendCardsAsync(
            claim,
            chats,
            cards,
            cancellationToken);
        await PersistDeliveryAsync(
            claim,
            delivery,
            chats.Count * cards.Count,
            cancellationToken);
        return true;
    }

    private async Task RunNotificationAsync(
        string sessionId,
        string turnId,
        Func<Task> action)
    {
        var key = $"{sessionId}\0{turnId}";
        lock (sync)
        {
            if (!notificationFlights.Add(key))
            {
                return;
            }
        }

        try
        {
            await action();
        }
        finally
        {
            lock (sync)
            {
                notificationFlights.Remove(key);
            }
        }
    }
}
