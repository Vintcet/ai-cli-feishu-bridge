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
        await SynchronizeTranscriptWatchAsync(runtimeEvent, cancellationToken);
        await SynchronizeUserPromptAsync(runtimeEvent, cancellationToken);
        await SynchronizeApprovalNotificationsAsync(runtimeEvent, cancellationToken);
        await SynchronizeInputNotificationsAsync(runtimeEvent, cancellationToken);
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
                if (await ShouldDeferCompletionAsync(
                        runtimeEvent,
                        cancellationToken))
                {
                    break;
                }
                await FinishActivityAsync(
                    runtimeEvent,
                    "本轮处理完成",
                    cancellationToken);
                await ResetAsync(runtimeEvent.Session!.ExternalId, cancellationToken);
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
                await ResetAsync(runtimeEvent.Session!.ExternalId, cancellationToken);
                fileTransfers?.RemoveSession(runtimeEvent.Session.ExternalId);
                break;
        }
    }

    private async Task SynchronizeApprovalNotificationsAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        if (approvalNotifications is null)
        {
            return;
        }
        try
        {
            switch (runtimeEvent.EventType)
            {
                case RuntimeEventTypes.ApprovalRequested:
                    await approvalNotifications.NotifyPendingAsync(
                        RequiredString(runtimeEvent.Payload, "requestId"),
                        runtimeEvent.Session!.ExternalId,
                        cancellationToken);
                    break;
                case RuntimeEventTypes.ApprovalResolvedExternally:
                    await approvalNotifications.SynchronizeAsync(
                        RequiredString(runtimeEvent.Payload, "requestId"),
                        runtimeEvent.Session!.ExternalId,
                        cancellationToken);
                    break;
                case RuntimeEventTypes.SessionEnded:
                case RuntimeEventTypes.RuntimeDisconnected:
                    await approvalNotifications.SynchronizeSessionAsync(
                        runtimeEvent.Session!.ExternalId,
                        cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Approval state is already durable. Feishu delivery and terminal
            // card synchronization are idempotent side channels and must not
            // reject the Runtime event.
        }
    }

    private async Task SynchronizeTranscriptWatchAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        if (transcriptMonitor is null ||
            !string.Equals(runtimeEvent.Runtime, RuntimeNames.Codex, StringComparison.Ordinal) ||
            runtimeEvent.Session is null)
        {
            return;
        }
        if (runtimeEvent.EventType == RuntimeEventTypes.SessionStarted)
        {
            await transcriptMonitor.WatchAsync(
                runtimeEvent.Session.ExternalId,
                OptionalString(runtimeEvent.Payload, "transcriptPath"),
                cancellationToken);
        }
        else if (runtimeEvent.EventType is RuntimeEventTypes.SessionEnded or
                 RuntimeEventTypes.RuntimeDisconnected)
        {
            await transcriptMonitor.UnwatchAsync(
                runtimeEvent.Session.ExternalId,
                cancellationToken);
        }
    }

    private async Task HandleTranscriptErrorAsync(
        CodexTranscriptErrorEvent transcriptError,
        CancellationToken cancellationToken)
    {
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(transcriptError.SessionId, out var session) ||
            session.Status == SessionStatuses.Ended)
        {
            return;
        }
        await HandleAsync(new RuntimeEventEnvelope
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = RuntimeNames.Codex,
            Session = new RuntimeSessionReference
            {
                ExternalId = session.SessionId,
                Cwd = session.Cwd,
            },
            EventId = $"codex-transcript-{transcriptError.SessionId}-{transcriptError.TurnId}",
            EventType = RuntimeEventTypes.TurnFailed,
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            TraceId = $"codex-transcript-{Guid.NewGuid():N}",
            CorrelationId = transcriptError.TurnId,
            Payload = JsonSerializer.SerializeToElement(new
            {
                turnId = transcriptError.TurnId,
                error = transcriptError.Error,
                code = transcriptError.ErrorCode,
                source = "codex_transcript",
            }),
        }, cancellationToken);
    }

    private async Task SynchronizeUserPromptAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        if (runtimeEvent.EventType != RuntimeEventTypes.TurnStarted ||
            runtimeEvent.Session is null ||
            OptionalString(runtimeEvent.Payload, "prompt") is not { Length: > 0 } prompt)
        {
            return;
        }
        var remotePromptKind = remotePrompts?.TryConsume(
            runtimeEvent.Session.ExternalId,
            prompt);
        if (remotePromptKind is not BridgeRemotePromptKind.AutomaticRetry)
        {
            await ResetAsync(runtimeEvent.Session.ExternalId, cancellationToken);
        }
        if (remotePromptKind is not null)
        {
            return;
        }
        try
        {
            var store = await storeOwner.ReadAsync(cancellationToken);
            if (store.Settings.NotifyUserPrompts != true ||
                !store.Sessions.Sessions.TryGetValue(
                    runtimeEvent.Session.ExternalId,
                    out var stored) ||
                !ExtensionBoolean(stored, "managedByAssistant") ||
                sessionGroups is null)
            {
                return;
            }
            var session = SessionView(stored);
            var card = renderer.UserPrompt(session, prompt);
            foreach (var chatId in (await sessionGroups.NotificationChatsAsync(
                    stored.SessionId,
                    cancellationToken))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal))
            {
                await gateway.SendCardAsync(
                    chatId,
                    card,
                    $"user-prompt:{runtimeEvent.EventId}:{chatId}",
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Prompt mirroring is optional and must not reject a valid Hook event.
        }
    }

    private async Task SynchronizeInputNotificationsAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        if (inputNotifications is null)
        {
            return;
        }
        try
        {
            switch (runtimeEvent.EventType)
            {
                case RuntimeEventTypes.InputRequested:
                    await inputNotifications.NotifyPendingInputAsync(
                        RequiredString(runtimeEvent.Payload, "requestId"),
                        runtimeEvent.Session!.ExternalId,
                        cancellationToken);
                    break;
                case RuntimeEventTypes.InputResolvedExternally:
                    await inputNotifications.SynchronizeInputAsync(
                        RequiredString(runtimeEvent.Payload, "requestId"),
                        runtimeEvent.Session!.ExternalId,
                        cancellationToken);
                    break;
                case RuntimeEventTypes.SessionEnded:
                case RuntimeEventTypes.RuntimeDisconnected:
                    await inputNotifications.SynchronizeInputSessionAsync(
                        runtimeEvent.Session!.ExternalId,
                        cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Input state is already durable. Feishu delivery and terminal card
            // synchronization are idempotent side channels and must not reject
            // the Runtime event.
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
}
