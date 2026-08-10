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

}
