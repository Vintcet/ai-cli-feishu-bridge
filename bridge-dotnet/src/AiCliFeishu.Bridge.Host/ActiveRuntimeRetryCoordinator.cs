using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal static class BridgeRetryStopKinds
{
    public const string Stopped = "stopped";
    public const string AlreadyStopped = "already_stopped";
    public const string Stale = "stale";
}

internal static class BridgeRuntimeNotificationKinds
{
    public const string Stop = "stop";
    public const string Error = "error";
}

internal sealed record BridgeRetryStopResult(
    string Kind,
    bool RetryAlreadyStarted,
    FeishuCardView? Card = null);

internal interface IBridgeActiveRuntimeRetryCoordinator
{
    bool HasActiveRetry(string sessionId);

    ValueTask BeginManualTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<BridgeRetryStopResult> StopAsync(
        string sessionId,
        string cycleId,
        string messageId,
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveRuntimeRetryCoordinator :
    IBridgeRuntimeEventHandler,
    IBridgeActiveRuntimeRetryCoordinator,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IDisposable
{
    private const string RetryPrompt =
        "刚才的请求因临时服务错误失败。请重试上一项任务，并继续从中断处执行。";
    private const int DefaultMaxAttempts = 3;
    private const int DefaultIntervalSeconds = 5;
    private const int DefaultJitterSeconds = 3;
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeActiveRuntimeStateSink stateSink;
    private readonly IBridgeProductionStoreOwner storeOwner;
    private readonly Func<IBridgeRuntimeCommandGateway> runtimeCommands;
    private readonly IFeishuGateway gateway;
    private readonly IFeishuCardRenderer renderer;
    private readonly ActiveRuntimeActivityCoordinator? activity;
    private readonly IBridgeActiveFileTransferCoordinator? fileTransfers;
    private readonly IBridgeActiveSessionGroupCoordinator? sessionGroups;
    private readonly TimeProvider clock;
    private readonly TimeSpan? retryDelayOverride;
    private readonly Func<int, int> selectJitter;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, RetryCycle> cycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> attemptCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> generations = new(StringComparer.Ordinal);
    private readonly HashSet<string> notificationFlights = new(StringComparer.Ordinal);
    private readonly HashSet<Task> workers = [];
    private readonly SemaphoreSlim cardPatchGate = new(1, 1);
    private RetryRegistryState retries = RetryRegistryState.Empty;
    private bool started;
    private bool disposed;

    public ActiveRuntimeRetryCoordinator(
        BridgeHostOptions options,
        IBridgeActiveRuntimeStateSink stateSink,
        IBridgeProductionStoreOwner storeOwner,
        IBridgeRuntimeCommandGateway runtimeCommands,
        IFeishuGateway gateway,
        IFeishuCardRenderer renderer,
        TimeProvider? timeProvider = null,
        TimeSpan? retryDelayOverride = null,
        Func<int, int>? jitterSelector = null,
        ActiveRuntimeActivityCoordinator? activity = null,
        IBridgeActiveFileTransferCoordinator? fileTransfers = null,
        IBridgeActiveSessionGroupCoordinator? sessionGroups = null)
        : this(
            options,
            stateSink,
            storeOwner,
            () => runtimeCommands,
            gateway,
            renderer,
            timeProvider,
            retryDelayOverride,
            jitterSelector,
            activity,
            fileTransfers,
            sessionGroups)
    {
        ArgumentNullException.ThrowIfNull(runtimeCommands);
    }

    internal ActiveRuntimeRetryCoordinator(
        BridgeHostOptions options,
        IBridgeActiveRuntimeStateSink stateSink,
        IBridgeProductionStoreOwner storeOwner,
        Func<IBridgeRuntimeCommandGateway> runtimeCommands,
        IFeishuGateway gateway,
        IFeishuCardRenderer renderer,
        TimeProvider? timeProvider = null,
        TimeSpan? retryDelayOverride = null,
        Func<int, int>? jitterSelector = null,
        ActiveRuntimeActivityCoordinator? activity = null,
        IBridgeActiveFileTransferCoordinator? fileTransfers = null,
        IBridgeActiveSessionGroupCoordinator? sessionGroups = null)
    {
        this.options = options;
        this.stateSink = stateSink;
        this.storeOwner = storeOwner;
        this.runtimeCommands = runtimeCommands ??
            throw new ArgumentNullException(nameof(runtimeCommands));
        this.gateway = gateway;
        this.renderer = renderer;
        this.activity = activity;
        this.fileTransfers = fileTransfers;
        this.sessionGroups = sessionGroups;
        clock = timeProvider ?? TimeProvider.System;
        this.retryDelayOverride = retryDelayOverride;
        selectJitter = jitterSelector ?? (maximum => Random.Shared.Next(maximum + 1));
    }

    public string Name => "active-runtime-retry";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                var active = cycles.Values.Count(cycle =>
                    !cycle.Stopped && cycle.Phase is not RetryCyclePhases.Stopped);
                return new(
                    Name,
                    started ? "ready" : "starting",
                    $"active={active} tracked={cycles.Count} workers={workers.Count}");
            }
        }
    }

    public bool HasActiveRetry(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        lock (sync)
        {
            return cycles.TryGetValue(sessionId, out var cycle) &&
                !cycle.Stopped &&
                cycle.Phase is not RetryCyclePhases.Stopped;
        }
    }

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

    public async Task HandleAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        EnsureStarted();
        var failureGeneration = runtimeEvent.EventType == RuntimeEventTypes.TurnFailed
            ? Generation(runtimeEvent.Session!.ExternalId)
            : 0;
        await stateSink.HandleAsync(runtimeEvent, cancellationToken);
        if (runtimeEvent.EventType == RuntimeEventTypes.SessionStarted &&
            sessionGroups is not null)
        {
            try
            {
                sessionGroups.ScheduleEnsure(runtimeEvent.Session!.ExternalId);
            }
            catch
            {
                // Session state is already durable. Group creation is a best-effort
                // side channel and must not turn a valid Hook/SSE event into a
                // runtime failure.
            }
        }

        switch (runtimeEvent.EventType)
        {
            case RuntimeEventTypes.TurnStarted:
            case RuntimeEventTypes.TurnActivity:
                await RecordActivityAsync(runtimeEvent, cancellationToken);
                break;
            case RuntimeEventTypes.TurnFailed:
                await RecordActivityAsync(runtimeEvent, cancellationToken);
                await FinishActivityAsync(
                    runtimeEvent,
                    "本轮发生错误",
                    cancellationToken);
                await ProcessFailureAsync(
                    Failure(runtimeEvent, failureGeneration),
                    cancellationToken);
                break;
            case RuntimeEventTypes.TurnCompleted:
                await FinishActivityAsync(
                    runtimeEvent,
                    "本轮处理完成",
                    cancellationToken);
                Reset(runtimeEvent.Session!.ExternalId);
                var completion = Completion(runtimeEvent);
                var directives = BridgeFileTransferProtocol.ExtractDirectives(
                    completion.Message);
                completion = completion with
                {
                    Message = string.IsNullOrWhiteSpace(directives.DisplayMessage)
                        ? CompletionFallback(runtimeEvent.Runtime)
                        : directives.DisplayMessage,
                };
                var notificationClaimed = await ProcessCompletionNotificationAsync(
                    completion,
                    cancellationToken);
                if (notificationClaimed)
                {
                    await SendCompletedFilesBestEffortAsync(
                        runtimeEvent.Session.ExternalId,
                        directives.Paths,
                        cancellationToken);
                }
                break;
            case RuntimeEventTypes.SessionEnded:
            case RuntimeEventTypes.RuntimeDisconnected:
                await FinishActivityAsync(
                    runtimeEvent,
                    "会话已结束",
                    cancellationToken);
                Reset(runtimeEvent.Session!.ExternalId);
                fileTransfers?.RemoveSession(runtimeEvent.Session.ExternalId);
                break;
        }
    }

    private async Task RecordActivityAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        if (activity is null)
        {
            return;
        }
        try
        {
            await activity.RecordAsync(runtimeEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Activity cards are an optional notification side channel. A
            // renderer, Store or Feishu failure must not change Runtime
            // state handling or suppress the completion/error notification.
        }
    }

    private async Task FinishActivityAsync(
        RuntimeEventEnvelope runtimeEvent,
        string label,
        CancellationToken cancellationToken)
    {
        if (activity is null || runtimeEvent.Session is null)
        {
            return;
        }
        var turnId = OptionalString(runtimeEvent.Payload, "turnId") ??
            runtimeEvent.CorrelationId;
        try
        {
            await activity.FinishAsync(
                runtimeEvent.Session.ExternalId,
                label,
                turnId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // See RecordActivityAsync: progress delivery is deliberately
            // best effort and cannot become a Runtime failure boundary.
        }
    }

    public ValueTask BeginManualTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();
        Reset(sessionId);
        return ValueTask.CompletedTask;
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
        var canRetry = settings.AutoRetry &&
            generation == failure.Generation &&
            existing?.Stopped != true &&
            retryCount < settings.MaxAttempts &&
            RuntimeErrorClassifier.IsRetryable(failure.Error, failure.ErrorCode) &&
            IsRuntimeReady(session);
        var attempt = retryCount + 1;
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

    private void StartWorker(RetryCycle cycle)
    {
        var worker = RunScheduledRetryAsync(cycle);
        workers.Add(worker);
        _ = ObserveWorkerAsync(worker);
    }

    private async Task ObserveWorkerAsync(Task worker)
    {
        try
        {
            await worker;
        }
        finally
        {
            lock (sync)
            {
                workers.Remove(worker);
            }
        }
    }

    private async Task RunScheduledRetryAsync(RetryCycle cycle)
    {
        try
        {
            while (true)
            {
                var remaining = cycle.DueAt - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                await Task.Delay(remaining, clock, lifetime.Token);
            }

            var store = await storeOwner.ReadAsync(lifetime.Token);
            if (!store.Sessions.Sessions.TryGetValue(cycle.SessionId, out var session) ||
                !RetryStillAllowed(cycle, session, store.Settings))
            {
                await StopCycleAsync(cycle, lifetime.Token);
                return;
            }

            lock (sync)
            {
                if (cycles.GetValueOrDefault(cycle.SessionId) != cycle ||
                    cycle.Stopped ||
                    cycle.Phase != RetryCyclePhases.Scheduled)
                {
                    return;
                }
                var claim = RetryStateMachine.ClaimDue(
                    retries,
                    cycle.TaskId,
                    clock.GetUtcNow());
                if (!claim.Value)
                {
                    return;
                }
                retries = claim.State;
                cycle.Phase = RetryCyclePhases.Running;
            }
            await PatchCycleCardsAsync(cycle, "running", lifetime.Token);

            try
            {
                await runtimeCommands().DispatchAsync(
                    RetryCommand(cycle, session),
                    lifetime.Token);
                lock (sync)
                {
                    if (retries.Tasks.GetValueOrDefault(cycle.TaskId)?.Status ==
                        RetryStatuses.Claimed)
                    {
                        retries = RetryStateMachine.Complete(
                            retries,
                            cycle.TaskId,
                            true,
                            clock.GetUtcNow());
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                lock (sync)
                {
                    if (retries.Tasks.GetValueOrDefault(cycle.TaskId)?.Status ==
                        RetryStatuses.Claimed)
                    {
                        retries = RetryStateMachine.Complete(
                            retries,
                            cycle.TaskId,
                            false,
                            clock.GetUtcNow());
                    }
                    cycle.Stopped = true;
                    cycle.Phase = RetryCyclePhases.Stopped;
                }
                await PatchCycleCardsAsync(cycle, "stopped", lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            await StopCycleAsync(cycle, CancellationToken.None);
        }
    }

    private bool RetryStillAllowed(
        RetryCycle cycle,
        SessionStoreRecord session,
        SettingsStoreDocument settingsDocument)
    {
        var settings = Settings(settingsDocument);
        bool structurallyAllowed;
        lock (sync)
        {
            structurallyAllowed = cycles.GetValueOrDefault(cycle.SessionId) == cycle &&
                !cycle.Stopped &&
                cycle.Attempt <= settings.MaxAttempts &&
                settings.AutoRetry;
        }
        return structurallyAllowed && IsRuntimeReady(session);
    }

    private async Task StopCycleAsync(RetryCycle cycle, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (cycles.GetValueOrDefault(cycle.SessionId) != cycle)
            {
                return;
            }
            cycle.Stopped = true;
            cycle.Phase = RetryCyclePhases.Stopped;
            retries = RetryStateMachine.CancelSession(
                retries,
                cycle.SessionId,
                clock.GetUtcNow());
        }
        await PatchCycleCardsAsync(cycle, "stopped", cancellationToken);
    }

    private async Task PatchCycleCardsAsync(
        RetryCycle cycle,
        string state,
        CancellationToken cancellationToken)
    {
        await cardPatchGate.WaitAsync(cancellationToken);
        try
        {
            NodeStoreSnapshot store;
            try
            {
                store = await storeOwner.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return;
            }
            if (!store.Sessions.Sessions.TryGetValue(cycle.SessionId, out var session))
            {
                return;
            }
            var effectiveState = state;
            lock (sync)
            {
                if (string.Equals(state, "running", StringComparison.Ordinal) &&
                    cycle.Stopped)
                {
                    effectiveState = "stopped";
                }
            }
            var cards = renderer.RuntimeError(
                SessionView(session),
                cycle.Error,
                View(cycle, effectiveState));
            RetryMessageTarget[] targets;
            lock (sync)
            {
                targets = cycle.Targets.ToArray();
            }
            foreach (var target in targets)
            {
                try
                {
                    await gateway.PatchCardAsync(
                        target.MessageId,
                        cards[target.CardIndex % cards.Count],
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                }
            }
        }
        finally
        {
            cardPatchGate.Release();
        }
    }

    private async Task<FeishuCardView?> ReplacementCardAsync(
        RetryCycle cycle,
        string messageId,
        CancellationToken cancellationToken)
    {
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(cycle.SessionId, out var session))
        {
            return null;
        }
        var cards = renderer.RuntimeError(
            SessionView(session),
            cycle.Error,
            View(cycle, "stopped"));
        int cardIndex;
        lock (sync)
        {
            cardIndex = cycle.Targets.FirstOrDefault(target => string.Equals(
                target.MessageId,
                messageId,
                StringComparison.Ordinal))?.CardIndex ?? cards.Count - 1;
        }
        return cards[Math.Clamp(cardIndex, 0, cards.Count - 1)];
    }

    private async Task<RuntimeNotification?> TryClaimNotificationAsync(
        RuntimeNotification notification,
        CancellationToken cancellationToken)
    {
        RuntimeNotification? claimed = null;
        await storeOwner.UpdateAsync(
            store =>
            {
                if (!store.Sessions.Sessions.TryGetValue(
                        notification.SessionId,
                        out var session))
                {
                    return store;
                }

                var existingTurn = ExtensionString(session, "lastNotificationTurnId");
                var existingStatus = ExtensionString(session, "lastNotificationStatus");
                var existingKind = ExtensionString(session, "pendingNotificationKind");
                if (existingKind is null &&
                    string.Equals(
                        existingStatus,
                        "pending",
                        StringComparison.Ordinal))
                {
                    existingKind = string.Equals(
                        session.Status,
                        SessionStatuses.Error,
                        StringComparison.Ordinal)
                        ? BridgeRuntimeNotificationKinds.Error
                        : BridgeRuntimeNotificationKinds.Stop;
                }
                if (string.Equals(
                        existingTurn,
                        notification.TurnId,
                        StringComparison.Ordinal))
                {
                    if (!string.Equals(
                            existingStatus,
                            "pending",
                            StringComparison.Ordinal) ||
                        existingKind is not null &&
                        !string.Equals(
                            existingKind,
                            notification.Kind,
                            StringComparison.Ordinal))
                    {
                        // A completed notification, or a different notification
                        // kind for the same turn, owns this turn permanently.
                        return store;
                    }

                    claimed = notification with
                    {
                        Message = ExtensionString(
                            session,
                            "pendingNotificationMessage") ?? notification.Message,
                    };
                }
                else
                {
                    claimed = notification;
                }

                return NodeStoreBusinessStateMerger.PatchSessionExtensions(
                    store,
                    notification.SessionId,
                    new Dictionary<string, JsonElement?>
                    {
                        ["lastNotificationTurnId"] =
                            JsonSerializer.SerializeToElement(notification.TurnId),
                        ["lastNotificationStatus"] =
                            JsonSerializer.SerializeToElement("pending"),
                        ["pendingNotificationKind"] =
                            JsonSerializer.SerializeToElement(notification.Kind),
                        ["pendingNotificationMessage"] =
                            JsonSerializer.SerializeToElement(claimed.Message),
                    });
            },
            cancellationToken);
        return claimed;
    }

    private async Task<IReadOnlyList<RetryMessageTarget>> SendCardsAsync(
        RuntimeNotification notification,
        IReadOnlyList<string> chatIds,
        IReadOnlyList<FeishuCardView> cards,
        CancellationToken cancellationToken)
    {
        var sent = new List<RetryMessageTarget>();
        foreach (var chatId in chatIds)
        {
            for (var index = 0; index < cards.Count; index++)
            {
                try
                {
                    var messageId = await gateway.SendCardAsync(
                        chatId,
                        cards[index],
                        NotificationKey(notification, chatId, index),
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(messageId))
                    {
                        sent.Add(new(messageId, chatId, index));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                }
            }
        }
        return sent;
    }

    private async Task PersistDeliveryAsync(
        RuntimeNotification notification,
        IReadOnlyList<RetryMessageTarget> delivered,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        await storeOwner.UpdateAsync(
            store =>
            {
                var updated = AddRoutes(store, notification, delivered);
                var complete = expectedCount > 0 && delivered.Count == expectedCount;
                if (expectedCount == 0)
                {
                    return ClearNotification(updated, notification.SessionId);
                }
                return complete
                    ? NodeStoreBusinessStateMerger.PatchSessionExtensions(
                        updated,
                        notification.SessionId,
                        new Dictionary<string, JsonElement?>
                        {
                            ["lastNotificationStatus"] =
                                JsonSerializer.SerializeToElement("sent"),
                            ["pendingNotificationKind"] = null,
                            ["pendingNotificationMessage"] = null,
                        })
                    : updated;
            },
            cancellationToken);
    }

    private static NodeStoreSnapshot AddRoutes(
        NodeStoreSnapshot store,
        RuntimeNotification notification,
        IReadOnlyList<RetryMessageTarget> delivered)
    {
        if (delivered.Count == 0)
        {
            return store;
        }
        var messages = new Dictionary<string, MessageRouteStoreRecord>(
            store.Routes.Messages,
            StringComparer.Ordinal);
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var target in delivered)
        {
            messages[target.MessageId] = new()
            {
                MessageId = target.MessageId,
                SessionId = notification.SessionId,
                ChatId = target.ChatId,
                Kind = notification.Kind,
                CreatedAt = createdAt,
            };
        }
        return store with
        {
            Routes = new()
            {
                Messages = messages,
                ProcessedInbound = new Dictionary<string, string>(
                    store.Routes.ProcessedInbound,
                    StringComparer.Ordinal),
                ExtensionData = CloneExtensions(store.Routes.ExtensionData),
            },
        };
    }

    private static NodeStoreSnapshot ClearNotification(
        NodeStoreSnapshot store,
        string sessionId) => NodeStoreBusinessStateMerger.PatchSessionExtensions(
            store,
            sessionId,
            new Dictionary<string, JsonElement?>
            {
                ["lastNotificationTurnId"] = null,
                ["lastNotificationStatus"] = null,
                ["pendingNotificationKind"] = null,
                ["pendingNotificationMessage"] = null,
            });

    private bool IsRuntimeReady(SessionStoreRecord session)
    {
        try
        {
            return RuntimeNames.All.Contains(Runtime(session)) &&
                runtimeCommands().IsReady(
                    Runtime(session),
                    new RuntimeSession(session.SessionId, session.Cwd));
        }
        catch
        {
            return false;
        }
    }

    private TimeSpan RetryDelay(RetrySettings settings)
    {
        if (retryDelayOverride is { } overridden)
        {
            return overridden <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : overridden;
        }
        var selected = settings.JitterSeconds == 0
            ? 0
            : Math.Clamp(selectJitter(settings.JitterSeconds), 0, settings.JitterSeconds);
        return TimeSpan.FromSeconds(settings.IntervalSeconds + selected);
    }

    private static RetrySettings Settings(SettingsStoreDocument settings) => new(
        settings.AutoRetryErrors == true,
        Math.Clamp(settings.RetryMaxAttempts ?? DefaultMaxAttempts, 1, 20),
        Math.Clamp(settings.RetryIntervalSeconds ?? DefaultIntervalSeconds, 1, 600),
        Math.Clamp(settings.RetryJitterSeconds ?? DefaultJitterSeconds, 0, 120));

    private async ValueTask<IReadOnlyList<string>> NotificationChatsAsync(
        NodeStoreSnapshot store,
        SessionStoreRecord session,
        CancellationToken cancellationToken)
    {
        if (sessionGroups is not null)
        {
            return await sessionGroups.NotificationChatsAsync(
                session.SessionId,
                cancellationToken);
        }
        if (ExtensionBoolean(session, "managedByAssistant") &&
            ExtensionString(session, "feishuChatId") is { } sessionChat)
        {
            return [sessionChat];
        }
        return store.Bindings.Users.Values
            .Select(binding => binding.ChatId)
            .Where(chatId => !string.IsNullOrWhiteSpace(chatId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private RuntimeCommandEnvelope RetryCommand(
        RetryCycle cycle,
        SessionStoreRecord session) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = cycle.Runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = cycle.SessionId,
                Cwd = session.Cwd,
            },
            TraceId = cycle.TraceId,
            CorrelationId = cycle.EventId,
            CommandId = $"runtime-retry-{cycle.CycleId}-{cycle.Attempt}",
            CommandType = RuntimeCommandTypes.PromptSend,
            CreatedAt = clock.GetUtcNow().ToString("O"),
            Payload = JsonSerializer.SerializeToElement(new
            {
                prompt = RetryPrompt,
                mode = "steer",
            }),
        };

    private static RuntimeNotification Completion(
        RuntimeEventEnvelope runtimeEvent) => new(
        runtimeEvent.Runtime,
        runtimeEvent.Session!.ExternalId,
        OptionalString(runtimeEvent.Payload, "turnId") ??
            runtimeEvent.CorrelationId ??
            runtimeEvent.EventId,
        BridgeRuntimeNotificationKinds.Stop,
        OptionalString(runtimeEvent.Payload, "message") ??
            CompletionFallback(runtimeEvent.Runtime),
        runtimeEvent.TraceId,
        runtimeEvent.EventId);

    private static RetryFailure Failure(
        RuntimeEventEnvelope runtimeEvent,
        long generation) => new(
        runtimeEvent.Runtime,
        runtimeEvent.Session!.ExternalId,
        OptionalString(runtimeEvent.Payload, "turnId") ??
            runtimeEvent.CorrelationId ??
            runtimeEvent.EventId,
        RequiredString(runtimeEvent.Payload, "error"),
        OptionalString(runtimeEvent.Payload, "code"),
        generation,
        runtimeEvent.TraceId,
        runtimeEvent.EventId);

    private static FeishuRuntimeRetryView View(RetryCycle cycle, string state) => new(
        cycle.CycleId,
        state,
        cycle.Attempt,
        cycle.MaxAttempts,
        Math.Max(0, (int)Math.Ceiling(cycle.Delay.TotalSeconds)));

    private static string CompletionFallback(RuntimeEventEnvelope runtimeEvent) =>
        CompletionFallback(runtimeEvent.Runtime);

    private static string CompletionFallback(SessionStoreRecord session) =>
        CompletionFallback(Runtime(session));

    private static string CompletionFallback(string runtime) =>
        $"{RuntimeDisplayName(runtime)} 已结束本轮处理。";

    private static FeishuSessionView SessionView(SessionStoreRecord session) => new(
        session.SessionId,
        Runtime(session),
        ExtensionString(session, "alias") ??
            session.ProjectName ??
            session.ShortId ??
            ShortId(session.SessionId),
        session.Cwd,
        ExtensionBoolean(session, "managedByAssistant"));

    private static string NotificationKey(
        RuntimeNotification notification,
        string chatId,
        int cardIndex)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{notification.SessionId}\0{notification.TurnId}\0{notification.Kind}\0{chatId}\0{cardIndex}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string CycleId(string sessionId, string turnId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\0{turnId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private void Reset(string sessionId)
    {
        lock (sync)
        {
            generations[sessionId] = GenerationLocked(sessionId) + 1;
            cycles.Remove(sessionId);
            attemptCounts.Remove(sessionId);
            retries = RetryStateMachine.CancelSession(retries, sessionId, clock.GetUtcNow());
        }
    }

    private long Generation(string sessionId)
    {
        lock (sync)
        {
            return GenerationLocked(sessionId);
        }
    }

    private long GenerationLocked(string sessionId) =>
        generations.GetValueOrDefault(sessionId);

    private void EnsureStarted()
    {
        EnsureActive();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started)
            {
                throw new InvalidOperationException("Active Runtime 重试协调器尚未启动。");
            }
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException("Runtime 自动重试只能用于 Active Host。");
        }
    }

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime) ? RuntimeNames.Codex : session.Runtime;

    private static string RuntimeDisplayName(string runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private static string RequiredString(JsonElement value, string name) =>
        value.GetProperty(name).GetString()!;

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : null;

    private static string? ExtensionString(ExtensibleStoreObject value, string name) =>
        value.ExtensionData?.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } item &&
        !string.IsNullOrWhiteSpace(item.Value.GetString())
            ? item.Value.GetString()!.Trim()
            : null;

    private static bool ExtensionBoolean(ExtensibleStoreObject value, string name) =>
        value.ExtensionData?.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.True };

    private static Dictionary<string, JsonElement>? CloneExtensions(
        Dictionary<string, JsonElement>? extensions) => extensions?.ToDictionary(
            item => item.Key,
            item => item.Value.Clone(),
            StringComparer.Ordinal);

    private static string ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            lifetime.Cancel();
        }
        lifetime.Dispose();
    }

    private static class RetryCyclePhases
    {
        public const string Preparing = "preparing";
        public const string Scheduled = "scheduled";
        public const string Running = "running";
        public const string Stopped = "stopped";
    }

    private sealed class RetryCycle(
        string cycleId,
        string taskId,
        string runtime,
        string sessionId,
        string turnId,
        string error,
        int attempt,
        int maxAttempts,
        TimeSpan delay,
        DateTimeOffset dueAt,
        string traceId,
        string eventId)
    {
        public string CycleId { get; } = cycleId;
        public string TaskId { get; } = taskId;
        public string Runtime { get; } = runtime;
        public string SessionId { get; } = sessionId;
        public string TurnId { get; } = turnId;
        public string Error { get; } = error;
        public int Attempt { get; } = attempt;
        public int MaxAttempts { get; } = maxAttempts;
        public TimeSpan Delay { get; } = delay;
        public DateTimeOffset DueAt { get; } = dueAt;
        public string TraceId { get; } = traceId;
        public string EventId { get; } = eventId;
        public string Phase { get; set; } = RetryCyclePhases.Preparing;
        public bool Stopped { get; set; }
        public bool WorkerScheduled { get; set; }
        public List<RetryMessageTarget> Targets { get; set; } = [];
    }

    private sealed record RetryFailure(
        string Runtime,
        string SessionId,
        string TurnId,
        string Error,
        string? ErrorCode,
        long Generation,
        string TraceId,
        string EventId);

    private sealed record RuntimeNotification(
        string Runtime,
        string SessionId,
        string TurnId,
        string Kind,
        string Message,
        string TraceId,
        string EventId);

    private sealed record RetryMessageTarget(
        string MessageId,
        string ChatId,
        int CardIndex);

    private sealed record RetrySettings(
        bool AutoRetry,
        int MaxAttempts,
        int IntervalSeconds,
        int JitterSeconds);
}
