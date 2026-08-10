using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuApprovalNotificationCoordinator
{
    public async Task NotifyPendingInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (inputStateOwner is null)
        {
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        await inputSynchronizationGate.WaitAsync(cancellationToken);
        try
        {
            await NotifyPendingInputCoreAsync(requestId, sessionId, cancellationToken);
        }
        finally
        {
            inputSynchronizationGate.Release();
        }
    }

    public async Task SynchronizeInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (inputStateOwner is null)
        {
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        await inputSynchronizationGate.WaitAsync(cancellationToken);
        try
        {
            await SynchronizeInputCoreAsync(requestId, sessionId, cancellationToken);
        }
        finally
        {
            inputSynchronizationGate.Release();
        }
    }

    public async Task SynchronizeInputSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (inputStateOwner is null)
        {
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        await inputSynchronizationGate.WaitAsync(cancellationToken);
        try
        {
            var current = inputStateOwner.Snapshot;
            if (!current.Initialized ||
                !current.Sessions.Sessions.ContainsKey(sessionId))
            {
                return;
            }
            Exception? firstFailure = null;
            foreach (var input in current.Inputs.Requests.Values.Where(input =>
                         string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) &&
                         IsTerminalInput(input)))
            {
                try
                {
                    await SynchronizeInputCoreAsync(
                        input.RequestId,
                        sessionId,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    Interlocked.Increment(ref inputSynchronizationFailures);
                    firstFailure ??= error;
                }
            }
            if (firstFailure is not null)
            {
                throw firstFailure;
            }
        }
        finally
        {
            inputSynchronizationGate.Release();
        }
    }

    private async Task NotifyPendingInputCoreAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var current = inputStateOwner!.Snapshot;
        if (!TryPendingInput(
                current,
                requestId,
                sessionId,
                out var input,
                out var session))
        {
            return;
        }
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!TryStoredSession(session, store, out var storedSession))
        {
            return;
        }
        var questions = InputQuestions(input);
        var chats = await sessionGroups.NotificationChatsAsync(
            sessionId,
            cancellationToken);
        if (chats.Count == 0)
        {
            await DeferInputToLocalCoreAsync(requestId, sessionId, cancellationToken);
            return;
        }

        var routes = InputRoutes(store, input, questions);
        var sessionView = SessionView(session, storedSession);
        Exception? firstFailure = null;
        foreach (var chatId in chats
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (var (question, questionIndex) in questions
                         .Select((question, index) => (question, index)))
            {
                if (routes.Any(route =>
                        string.Equals(route.ChatId, chatId, StringComparison.Ordinal) &&
                        string.Equals(route.Target.QuestionId, question.Id, StringComparison.Ordinal)))
                {
                    continue;
                }
                try
                {
                    var card = renderer.PendingInput(
                        sessionView,
                        requestId,
                        question,
                        questionIndex,
                        questions.Length,
                        selectionKey: chatId);
                    var messageId = await gateway.SendCardAsync(
                        chatId,
                        card,
                        InputNotificationKey(requestId, chatId, question.Id),
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(messageId))
                    {
                        throw new InvalidOperationException("飞书补充信息卡片未返回消息 ID。");
                    }
                    await storeOwner.UpdateAsync(
                        currentStore => AddInputRoute(
                            currentStore,
                            requestId,
                            sessionId,
                            messageId,
                            chatId,
                            question.Id,
                            chatId,
                            DateTimeOffset.UtcNow),
                        cancellationToken);
                    routes.Add(new(
                        chatId,
                        new(
                            messageId,
                            question.Id,
                            questionIndex,
                            chatId)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    Interlocked.Increment(ref inputSynchronizationFailures);
                    firstFailure ??= error;
                }
            }
        }

        // A user can answer while the send loop is still running. Re-read the
        // authoritative projection and immediately remove buttons from every
        // card that was delivered before the answer won the race.
        var observed = inputStateOwner.Snapshot;
        if (TryTerminalInput(
                observed,
                requestId,
                sessionId,
                out _,
                out _))
        {
            await SynchronizeInputCoreAsync(requestId, sessionId, cancellationToken);
        }
        else if (questions.Any(question => !routes.Any(route => string.Equals(
                     route.Target.QuestionId,
                     question.Id,
                     StringComparison.Ordinal))))
        {
            await DeferInputToLocalCoreAsync(requestId, sessionId, cancellationToken);
            return;
        }
        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private async Task DeferInputToLocalCoreAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var claim = await inputStateOwner!.TryClaimInputAsync(
            requestId,
            sessionId,
            cancellationToken);
        if (claim is null)
        {
            await SynchronizeInputCoreAsync(requestId, sessionId, cancellationToken);
            return;
        }

        var completed = false;
        try
        {
            if (claim.Session.Runtime is RuntimeNames.Codex or RuntimeNames.ClaudeCode)
            {
                var managedHookSink = managedHooks?.Invoke();
                if (managedHookSink is null)
                {
                    return;
                }
                await managedHookSink.DeferInputToLocalAsync(
                    claim.Session.Runtime,
                    claim.Session.SessionId,
                    claim.Input.RequestId,
                    cancellationToken);
            }
            var deferred = await inputStateOwner.DeferClaimedInputAsync(
                requestId,
                sessionId,
                CancellationToken.None);
            completed = deferred is not null;
            if (deferred is not null)
            {
                await SynchronizeInputCoreAsync(
                    requestId,
                    sessionId,
                    cancellationToken);
            }
        }
        finally
        {
            if (!completed)
            {
                await inputStateOwner.ReleaseInputClaimAsync(
                    requestId,
                    CancellationToken.None);
            }
        }
    }

    private async Task SynchronizeInputCoreAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var current = inputStateOwner!.Snapshot;
        if (!TryTerminalInput(
                current,
                requestId,
                sessionId,
                out var input,
                out var session))
        {
            return;
        }
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!TryStoredSession(session, store, out var storedSession))
        {
            return;
        }
        var questions = InputQuestions(input);
        var targets = InputRoutes(store, input, questions)
            .Select(route => route.Target)
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }
        await interactions.SynchronizeInputAsync(
            input,
            SessionView(session, storedSession),
            questions,
            targets,
            cancellationToken);
    }
}
