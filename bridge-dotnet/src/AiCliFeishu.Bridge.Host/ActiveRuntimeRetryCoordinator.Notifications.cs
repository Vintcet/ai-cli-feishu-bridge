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

                return BridgeStoreBusinessStateMerger.PatchSessionExtensions(
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
                    ? BridgeStoreBusinessStateMerger.PatchSessionExtensions(
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

    private static BridgeStoreSnapshot AddRoutes(
        BridgeStoreSnapshot store,
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

    private static BridgeStoreSnapshot ClearNotification(
        BridgeStoreSnapshot store,
        string sessionId) => BridgeStoreBusinessStateMerger.PatchSessionExtensions(
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
}
