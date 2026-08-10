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
/// The event list intentionally stays in memory, while
/// the activity key and delivered message routes are written to the compatible
/// Store.  That gives a restart a safe way to patch an already-created card
/// without persisting tool output or other potentially sensitive detail.
/// </summary>
internal sealed partial class ActiveRuntimeActivityCoordinator :
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
                    return BridgeStoreBusinessStateMerger.PatchSessionExtensions(
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
                    return BridgeStoreBusinessStateMerger.PatchSessionExtensions(
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
        BridgeStoreSnapshot store)
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
        BridgeStoreSnapshot store,
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

    private async Task<BridgeStoreSnapshot?> TryReadStoreAsync(
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
        BridgeStoreSnapshot store,
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
