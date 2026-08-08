using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Owns the ephemeral, per-turn progress card.  Runtime adapters only emit
/// standard activity events; this coordinator is the one place that decides
/// when a progress card is created, patched, completed, or retried.
///
/// The event list intentionally stays in memory (as in the Node owner), while
/// the activity key and delivered message routes are written to the compatible
/// Store.  That gives a restart a safe way to patch an already-created card
/// without persisting tool output or other potentially sensitive detail.
/// </summary>
internal sealed class ActiveRuntimeActivityCoordinator :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IDisposable
{
    private const int MaximumEvents = 6;
    private const string ActivityRouteKind = "activity";
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeProductionStoreOwner storeOwner;
    private readonly IFeishuGateway gateway;
    private readonly IFeishuCardRenderer renderer;
    private readonly IBridgeActiveSessionGroupCoordinator? sessionGroups;
    private readonly TimeProvider clock;
    private readonly TimeSpan flushInterval;
    private readonly TimeSpan retryInterval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, ActivityState> states =
        new(StringComparer.Ordinal);
    private readonly HashSet<Task> workers = [];
    private bool started;
    private bool disposed;

    public ActiveRuntimeActivityCoordinator(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IFeishuGateway gateway,
        IFeishuCardRenderer renderer,
        TimeProvider? timeProvider = null,
        TimeSpan? flushInterval = null,
        TimeSpan? retryInterval = null,
        IBridgeActiveSessionGroupCoordinator? sessionGroups = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.storeOwner = storeOwner ?? throw new ArgumentNullException(nameof(storeOwner));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.sessionGroups = sessionGroups;
        clock = timeProvider ?? TimeProvider.System;
        this.flushInterval = Positive(
            flushInterval ?? TimeSpan.FromSeconds(2),
            nameof(flushInterval));
        this.retryInterval = Positive(
            retryInterval ?? TimeSpan.FromSeconds(2),
            nameof(retryInterval));
    }

    public string Name => "active-runtime-activity";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                var active = states.Values.Count(state => !state.Completed);
                return new(
                    Name,
                    started ? "ready" : "starting",
                    $"active={active} tracked={states.Count} workers={workers.Count}");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return Task.CompletedTask;
            }
            started = true;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Task[] pending;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            started = false;
            lifetime.Cancel();
            pending = workers.ToArray();
        }
        try
        {
            await Task.WhenAll(pending);
        }
        catch
        {
            // Activity delivery is best effort and must not block Host shutdown.
        }
        lock (sync)
        {
            foreach (var state in states.Values)
            {
                state.FlushGate.Dispose();
            }
            states.Clear();
            workers.Clear();
        }
    }

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

        NodeStoreSnapshot store;
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

        NodeStoreSnapshot store;
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

    private async Task CompleteDetachedAsync(
        ActivityState state,
        string label,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (!state.Completed)
            {
                state.Completed = true;
                state.Events.Add(new(clock.GetUtcNow().ToString("O"), label));
                if (state.Events.Count > MaximumEvents)
                {
                    state.Events.RemoveRange(0, state.Events.Count - MaximumEvents);
                }
                state.Revision++;
            }
        }
        await FlushStateAsync(state, force: true, cancellationToken);
    }

    private void ScheduleFlush(ActivityState state)
    {
        TimeSpan delay;
        lock (sync)
        {
            if (!started || state.Completed && state.SentRevision >= state.Revision ||
                state.FlushTask is not null)
            {
                return;
            }
            delay = state.DeliveryFailed
                ? retryInterval
                : state.LastSentAt == DateTimeOffset.MinValue
                    ? TimeSpan.Zero
                    : Max(TimeSpan.Zero, flushInterval -
                        (clock.GetUtcNow() - state.LastSentAt));
            var worker = Task.Run(
                () => ScheduledFlushAsync(state, delay),
                CancellationToken.None);
            state.FlushTask = worker;
            workers.Add(worker);
        }
    }

    private async Task ScheduledFlushAsync(ActivityState state, TimeSpan delay)
    {
        try
        {
            // Ensure ScheduleFlush has published FlushTask before a zero-delay
            // first flush can reach the finally block.
            await Task.Yield();
            await Task.Delay(delay, clock, lifetime.Token);
            await FlushStateAsync(state, force: false, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            lock (sync)
            {
                state.DeliveryFailed = true;
            }
        }
        finally
        {
            lock (sync)
            {
                if (state.FlushTask is { } scheduled)
                {
                    workers.Remove(scheduled);
                    state.FlushTask = null;
                }
                if (started &&
                    states.GetValueOrDefault(state.SessionId) == state &&
                    (state.DeliveryFailed || state.SentRevision < state.Revision) &&
                    !(state.Completed && state.SentRevision >= state.Revision))
                {
                    ScheduleFlush(state);
                }
            }
        }
    }

    private async Task<bool> FlushStateAsync(
        ActivityState state,
        bool force,
        CancellationToken cancellationToken)
    {
        await state.FlushGate.WaitAsync(cancellationToken);
        try
        {
            long revision;
            IReadOnlyList<FeishuActivityEventView> events;
            string startedAt;
            bool completed;
            lock (sync)
            {
                if (!force &&
                    !state.DeliveryFailed &&
                    state.SentRevision >= state.Revision)
                {
                    return true;
                }
                revision = state.Revision;
                events = state.Events.ToArray();
                startedAt = state.StartedAt;
                completed = state.Completed;
                state.DeliveryFailed = false;
            }

            var store = await TryReadStoreAsync(cancellationToken);
            if (store is null)
            {
                MarkDeliveryFailed(state);
                return false;
            }
            if (!store.Sessions.Sessions.TryGetValue(
                    state.SessionId,
                    out var session))
            {
                lock (sync)
                {
                    state.SentRevision = Math.Max(state.SentRevision, revision);
                    state.DeliveryFailed = false;
                }
                RemoveIfCompleted(state);
                return true;
            }

            var chats = await NotificationChatsAsync(
                store,
                session,
                cancellationToken);
            if (chats.Count == 0)
            {
                lock (sync)
                {
                    state.SentRevision = revision;
                    state.LastSentAt = clock.GetUtcNow();
                }
                await ClearMarkerIfCurrentAsync(state, cancellationToken);
                RemoveIfCompleted(state);
                return true;
            }

            FeishuCardView card;
            try
            {
                card = renderer.RuntimeActivity(
                    SessionView(session),
                    events,
                    startedAt,
                    completed);
            }
            catch
            {
                MarkDeliveryFailed(state);
                return false;
            }

            var allSucceeded = true;
            foreach (var chatId in chats)
            {
                ActivityDelivery? delivery;
                lock (sync)
                {
                    delivery = state.Deliveries.GetValueOrDefault(chatId);
                    if (delivery is not null &&
                        delivery.SentRevision >= revision &&
                        delivery.RoutePersisted)
                    {
                        continue;
                    }
                }

                try
                {
                    string messageId;
                    var sentRevision = delivery?.SentRevision ?? -1;
                    var routePersisted = delivery?.RoutePersisted ?? false;
                    if (delivery?.MessageId is { Length: > 0 } existing)
                    {
                        if (sentRevision < revision)
                        {
                            await gateway.PatchCardAsync(existing, card, cancellationToken);
                            sentRevision = revision;
                        }
                        messageId = existing;
                    }
                    else
                    {
                        messageId = await gateway.SendCardAsync(
                            chatId,
                            card,
                            NotificationKey(state, chatId),
                            cancellationToken);
                        sentRevision = revision;
                    }
                    if (string.IsNullOrWhiteSpace(messageId))
                    {
                        throw new InvalidOperationException("飞书活动卡片未返回消息 ID。");
                    }
                    lock (sync)
                    {
                        state.Deliveries[chatId] = new(
                            messageId,
                            sentRevision,
                            routePersisted);
                    }
                    if (!routePersisted)
                    {
                        await PersistRouteAsync(
                            state,
                            messageId,
                            chatId,
                            cancellationToken);
                        lock (sync)
                        {
                            if (state.Deliveries.TryGetValue(chatId, out var current) &&
                                string.Equals(
                                    current.MessageId,
                                    messageId,
                                    StringComparison.Ordinal))
                            {
                                state.Deliveries[chatId] = current with
                                {
                                    RoutePersisted = true,
                                };
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    allSucceeded = false;
                }
            }

            lock (sync)
            {
                state.LastSentAt = clock.GetUtcNow();
                state.DeliveryFailed = !allSucceeded;
                if (allSucceeded)
                {
                    state.SentRevision = Math.Max(state.SentRevision, revision);
                }
            }
            if (allSucceeded && completed)
            {
                await ClearMarkerIfCurrentAsync(state, cancellationToken);
                RemoveIfCompleted(state);
            }
            return allSucceeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            MarkDeliveryFailed(state);
            return false;
        }
        finally
        {
            state.FlushGate.Release();
        }
    }

    private async Task PersistRouteAsync(
        ActivityState state,
        string messageId,
        string chatId,
        CancellationToken cancellationToken)
    {
        await storeOwner.UpdateAsync(
            store =>
            {
                if (!store.Sessions.Sessions.ContainsKey(state.SessionId))
                {
                    return store;
                }
                var messages = new Dictionary<string, MessageRouteStoreRecord>(
                    store.Routes.Messages,
                    StringComparer.Ordinal)
                {
                    [messageId] = new()
                    {
                        MessageId = messageId,
                        SessionId = state.SessionId,
                        ChatId = chatId,
                        Kind = ActivityRouteKind,
                        RequestId = state.ActivityKey,
                        CreatedAt = clock.GetUtcNow().ToString("O"),
                    },
                };
                return store with
                {
                    Routes = new()
                    {
                        Messages = messages,
                        ProcessedInbound = new Dictionary<string, string>(
                            store.Routes.ProcessedInbound,
                            StringComparer.Ordinal),
                        ExtensionData = Clone(store.Routes.ExtensionData),
                    },
                };
            },
            cancellationToken);
    }

    private async Task PersistMarkerAsync(
        ActivityState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await storeOwner.UpdateAsync(
                store =>
                {
                    if (!store.Sessions.Sessions.ContainsKey(state.SessionId))
                    {
                        return store;
                    }
                    return NodeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        state.SessionId,
                        new Dictionary<string, JsonElement?>
                        {
                            ["activeActivityKey"] =
                                JsonSerializer.SerializeToElement(state.ActivityKey),
                            ["activeActivityTurnId"] = state.TurnId is null
                                ? null
                                : JsonSerializer.SerializeToElement(state.TurnId),
                            ["activeActivityStartedAt"] =
                                JsonSerializer.SerializeToElement(state.StartedAt),
                            ["activeActivityStatus"] =
                                JsonSerializer.SerializeToElement("active"),
                        });
                },
                cancellationToken);
        }
        catch
        {
            // The in-memory state remains useful; a failed marker only removes
            // restart recovery, not the runtime event or its card delivery.
        }
    }

    private async Task ClearMarkerIfCurrentAsync(
        ActivityState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await storeOwner.UpdateAsync(
                store =>
                {
                    if (!store.Sessions.Sessions.TryGetValue(
                            state.SessionId,
                            out var session) ||
                        !string.Equals(
                            ExtensionString(session, "activeActivityKey"),
                            state.ActivityKey,
                            StringComparison.Ordinal))
                    {
                        return store;
                    }
                    return NodeStoreBusinessStateMerger.PatchSessionExtensions(
                        store,
                        state.SessionId,
                        new Dictionary<string, JsonElement?>
                        {
                            ["activeActivityKey"] = null,
                            ["activeActivityTurnId"] = null,
                            ["activeActivityStartedAt"] = null,
                            ["activeActivityStatus"] = null,
                        });
                },
                cancellationToken);
        }
        catch
        {
            // A later event or the next process start can retry the cleanup.
        }
    }

    private ActivityState? Rehydrate(
        SessionStoreRecord session,
        NodeStoreSnapshot store)
    {
        var key = ExtensionString(session, "activeActivityKey");
        var startedAt = ExtensionString(session, "activeActivityStartedAt");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(startedAt))
        {
            return null;
        }
        var state = new ActivityState(
            session.SessionId,
            key,
            ExtensionString(session, "activeActivityTurnId"),
            startedAt,
            string.Equals(
                ExtensionString(session, "activeActivityStatus"),
                "completed",
                StringComparison.Ordinal));
        RestoreRoutes(state, store, session.SessionId);
        return state;
    }

    private static void RestoreRoutes(
        ActivityState state,
        NodeStoreSnapshot store,
        string sessionId)
    {
        foreach (var route in store.Routes.Messages.Values.Where(route =>
                     route.SessionId == sessionId &&
                     route.Kind == ActivityRouteKind &&
                     string.Equals(route.RequestId, state.ActivityKey, StringComparison.Ordinal)))
        {
            // A persisted route proves that the message exists, but it does
            // not prove that the current in-memory revision was delivered
            // after a restart. The next activity/completion must patch it.
            state.Deliveries[route.ChatId] = new(route.MessageId, -1, true);
        }
    }

    private void RemoveIfCompleted(ActivityState state)
    {
        lock (sync)
        {
            if (state.Completed && states.GetValueOrDefault(state.SessionId) == state)
            {
                states.Remove(state.SessionId);
            }
        }
    }

    private void MarkDeliveryFailed(ActivityState state)
    {
        lock (sync)
        {
            state.DeliveryFailed = true;
            state.LastSentAt = clock.GetUtcNow();
        }
    }

    private async Task<NodeStoreSnapshot?> TryReadStoreAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await storeOwner.ReadAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsActivityEvent(RuntimeEventEnvelope runtimeEvent) =>
        (runtimeEvent.EventType is RuntimeEventTypes.TurnActivity or
            RuntimeEventTypes.TurnStarted or RuntimeEventTypes.TurnFailed) &&
            runtimeEvent.Payload.ValueKind == JsonValueKind.Object &&
            (OptionalString(runtimeEvent.Payload, "summary") is not null ||
             OptionalString(runtimeEvent.Payload, "activityKind") is not null ||
             runtimeEvent.EventType == RuntimeEventTypes.TurnFailed);

    private static FeishuActivityEventView ToActivityEvent(
        RuntimeEventEnvelope runtimeEvent)
    {
        var payload = runtimeEvent.Payload;
        var kind = OptionalString(payload, "activityKind");
        var tool = OptionalString(payload, "toolName");
        var summary = OptionalString(payload, "summary");
        var label = kind switch
        {
            RuntimeActivityKinds.ToolStarted =>
                $"正在调用 {HumanizeToolName(tool)}",
            RuntimeActivityKinds.ToolCompleted =>
                $"{HumanizeToolName(tool)} 已完成",
            RuntimeActivityKinds.ToolFailed =>
                $"{HumanizeToolName(tool)} 执行失败",
            RuntimeActivityKinds.ContextCompacting => "正在压缩上下文",
            RuntimeActivityKinds.ContextCompacted => "上下文压缩完成",
            RuntimeActivityKinds.PromptSubmitted => "已提交新任务",
            _ => summary ?? (runtimeEvent.EventType == RuntimeEventTypes.TurnFailed
                ? "本轮发生错误"
                : "活动更新"),
        };
        var detail = OptionalString(payload, "detail") ??
            (runtimeEvent.EventType == RuntimeEventTypes.TurnFailed
                ? OptionalString(payload, "error")
                : null);
        return new(
            runtimeEvent.OccurredAt,
            label,
            detail);
    }

    private static string HumanizeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "工具";
        }
        return toolName switch
        {
            "shell_command" => "命令行",
            "apply_patch" => "文件修改",
            "view_image" => "图片查看",
            "request_user_input" => "用户提问",
            var value when value.StartsWith("mcp__", StringComparison.Ordinal) =>
                $"MCP · {value[5..]}",
            var value => value,
        };
    }

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

    private static FeishuSessionView SessionView(SessionStoreRecord session) => new(
        session.SessionId,
        Runtime(session),
        ExtensionString(session, "alias") ??
            session.ProjectName ??
            session.ShortId ??
            ShortId(session.SessionId),
        session.Cwd,
        ExtensionBoolean(session, "managedByAssistant"));

    private static string NotificationKey(ActivityState state, string chatId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{state.SessionId}\0{state.ActivityKey}\0{ActivityRouteKind}\0{chatId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string ActivityKey(
        string sessionId,
        string? turnId,
        string eventId)
    {
        var source = turnId is null
            ? $"{sessionId}\0event\0{eventId}"
            : $"{sessionId}\0turn\0{turnId}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static ActivityMarker? ReadActivityMarker(SessionStoreRecord session)
    {
        var key = ExtensionString(session, "activeActivityKey");
        var startedAt = ExtensionString(session, "activeActivityStartedAt");
        return string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(startedAt)
            ? null
            : new(
                key,
                ExtensionString(session, "activeActivityTurnId"),
                startedAt,
                ExtensionString(session, "activeActivityStatus") ?? "active");
    }

    private static string Runtime(SessionStoreRecord session) =>
        RuntimeNames.All.Contains(session.Runtime ?? string.Empty)
            ? session.Runtime!
            : RuntimeNames.Codex;

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ExtensionString(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ExtensionBoolean(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static Dictionary<string, JsonElement>? Clone(
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

    private static TimeSpan Positive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left > right ? left : right;

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "Runtime 活动通知只能用于 Active Host。");
        }
    }

    private void EnsureStarted()
    {
        EnsureActive();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started)
            {
                throw new InvalidOperationException("Runtime 活动通知尚未启动。");
            }
        }
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

    private sealed class ActivityState(
        string sessionId,
        string activityKey,
        string? turnId,
        string startedAt,
        bool completed)
    {
        public string SessionId { get; } = sessionId;
        public string ActivityKey { get; } = activityKey;
        public string? TurnId { get; set; } = turnId;
        public string StartedAt { get; } = startedAt;
        public List<FeishuActivityEventView> Events { get; } = [];
        public Dictionary<string, ActivityDelivery> Deliveries { get; } =
            new(StringComparer.Ordinal);
        public long Revision { get; set; }
        public long SentRevision { get; set; } = -1;
        public DateTimeOffset LastSentAt { get; set; } = DateTimeOffset.MinValue;
        public bool Completed { get; set; } = completed;
        public bool DeliveryFailed { get; set; }
        public Task? FlushTask { get; set; }
        public SemaphoreSlim FlushGate { get; } = new(1, 1);
    }

    private sealed record ActivityDelivery(
        string MessageId,
        long SentRevision,
        bool RoutePersisted = false);

    private sealed record ActivityMarker(
        string Key,
        string? TurnId,
        string StartedAt,
        string Status);
}
