using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveRuntimeRetryCoordinator
{
    private async Task<bool> ShouldDeferCompletionAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        if (transcriptMonitor is null ||
            runtimeEvent.Runtime is not RuntimeNames.Codex ||
            runtimeEvent.Session is null)
        {
            return false;
        }

        // Stop hooks can arrive before the background transcript poll observes
        // a task_complete record already written by Codex. Scan synchronously
        // before treating Stop as success.
        await transcriptMonitor.CheckNowAsync(cancellationToken);
        if (await HasRecordedFailureAsync(runtimeEvent, cancellationToken))
        {
            return true;
        }

        // A failed Codex turn can emit Stop before task_complete(error) reaches
        // the transcript monitor. An empty last_assistant_message is not a
        // positive success signal, so leave the retry cycle intact and let the
        // later transcript error own the turn.
        return HasActiveRetry(runtimeEvent.Session.ExternalId) &&
            OptionalString(runtimeEvent.Payload, "message") is null;
    }

    private async Task<bool> HasRecordedFailureAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken)
    {
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(
                runtimeEvent.Session!.ExternalId,
                out var session))
        {
            return false;
        }
        if (string.Equals(session.Status, SessionStatuses.Error, StringComparison.Ordinal))
        {
            return true;
        }
        var turnId = OptionalString(runtimeEvent.Payload, "turnId") ??
            runtimeEvent.CorrelationId;
        if (string.IsNullOrWhiteSpace(turnId) ||
            !string.Equals(
                ExtensionString(session, "lastNotificationTurnId"),
                turnId,
                StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(
                ExtensionString(session, "pendingNotificationKind"),
                BridgeRuntimeNotificationKinds.Error,
                StringComparison.Ordinal))
        {
            return true;
        }
        return store.Routes.Messages.Values.Any(route =>
            string.Equals(route.SessionId, session.SessionId, StringComparison.Ordinal) &&
            string.Equals(
                route.Kind,
                BridgeRuntimeNotificationKinds.Error,
                StringComparison.Ordinal));
    }
}
