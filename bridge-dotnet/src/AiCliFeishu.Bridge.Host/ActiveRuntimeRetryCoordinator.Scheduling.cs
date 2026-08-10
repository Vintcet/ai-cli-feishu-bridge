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
            if (!await PersistRetryStateAsync(
                    cycle,
                    PersistedRetryPhases.Running,
                    lifetime.Token))
            {
                return;
            }
            await PatchCycleCardsAsync(cycle, "running", lifetime.Token);

            var dispatched = false;
            remotePrompts?.Remember(
                cycle.SessionId,
                RetryPrompt,
                BridgeRemotePromptKind.AutomaticRetry);
            try
            {
                await runtimeCommands().DispatchAsync(
                    RetryCommand(cycle, session),
                    lifetime.Token);
                dispatched = true;
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
                await PersistRetryStateAsync(
                    cycle,
                    PersistedRetryPhases.Dispatched,
                    lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                if (!dispatched)
                {
                    remotePrompts?.Forget(
                        cycle.SessionId,
                        RetryPrompt,
                        BridgeRemotePromptKind.AutomaticRetry);
                }
                return;
            }
            catch
            {
                if (dispatched)
                {
                    // The prompt has already crossed the Runtime boundary. Keep
                    // the ledger entry so its UserPromptSubmit cannot be mistaken
                    // for a manual turn even if the durable phase update failed.
                    return;
                }
                remotePrompts?.Forget(
                    cycle.SessionId,
                    RetryPrompt,
                    BridgeRemotePromptKind.AutomaticRetry);
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
                await PersistRetryStateAsync(
                    cycle,
                    PersistedRetryPhases.Stopped,
                    lifetime.Token);
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
        await PersistRetryStateAsync(
            cycle,
            PersistedRetryPhases.Stopped,
            cancellationToken);
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
            BridgeStoreSnapshot store;
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
}
