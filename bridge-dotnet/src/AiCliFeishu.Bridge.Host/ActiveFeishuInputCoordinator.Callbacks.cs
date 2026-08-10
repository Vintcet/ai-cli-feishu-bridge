using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuInputCoordinator
{
    public async Task<FeishuCallbackResult> HandleAsync(
        FeishuIntent intent,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Parameter(intent, "requestId");
        var sessionId = Parameter(intent, "sessionId");
        if (requestId is null || sessionId is null)
        {
            return new("error", "问答参数不完整。");
        }
        var context = TryContext(requestId, sessionId, store);
        if (context is null)
        {
            return new("warning", "这组问题已经处理或失效。");
        }
        RestoreTargets(context, store);

        var questionId = Parameter(intent, "questionId");
        var selectionKey = SelectionKey(intent);
        if (selectionKey is null)
        {
            return new("warning", "这张问题卡已经失效。");
        }
        RegisterTarget(context, intent.MessageId, questionId, selectionKey);

        if (context.Input.Status != InputRequestStatuses.Pending)
        {
            var observedTargets = TargetsSnapshot(requestId);
            return await SynchronizeForCallbackAsync(
                intent,
                new(
                    "warning",
                    "这组问题已经处理或失效。",
                    TerminalInputCard(context, questionId)),
                async synchronizationCancellationToken =>
                {
                    await interactions.SynchronizeInputAsync(
                        context.Input,
                        context.SessionView,
                        context.Questions,
                        observedTargets,
                        synchronizationCancellationToken);
                    ClearInteraction(requestId);
                },
                cancellationToken);
        }
        if (clock.GetUtcNow() >= context.Input.ExpiresAt)
        {
            return new("warning", "这组问题已经超时，请回到电脑端处理。");
        }

        return intent.IntentType switch
        {
            FeishuIntentTypes.InputDeferToLocal => await DeferAsync(
                intent,
                context,
                cancellationToken),
            FeishuIntentTypes.InputToggle => await ToggleAsync(
                intent,
                context,
                selectionKey,
                cancellationToken),
            FeishuIntentTypes.InputSubmit => await SubmitAsync(
                intent,
                context,
                selectionKey,
                cancellationToken),
            FeishuIntentTypes.InputAnswer => await AnswerOptionAsync(
                intent,
                context,
                selectionKey,
                cancellationToken),
            _ => throw new NotSupportedException(
                $"Active Host 问答协调器不支持意图 {intent.IntentType}。"),
        };
    }

    public async Task<bool> TryHandleQuotedReplyAsync(
        FeishuIntent intent,
        BridgeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(store);
        var quoted = ActiveFeishuQuotedRouteLookup.Find(intent, store.Routes);
        if (quoted is null ||
            !string.Equals(quoted.Route.Kind, "input", StringComparison.Ordinal))
        {
            return false;
        }
        var route = quoted.Route;
        var quotedMessageId = quoted.MessageId;
        if (string.IsNullOrWhiteSpace(route.RequestId))
        {
            await RespondAsync(intent, "这张问题卡缺少请求信息，已无法回答。", cancellationToken);
            return true;
        }
        var context = TryContext(route.RequestId, route.SessionId, store);
        if (context is null || context.Input.Status != InputRequestStatuses.Pending)
        {
            await RespondAsync(intent, "这组问题已经处理或失效。", cancellationToken);
            return true;
        }
        RestoreTargets(context, store);
        var questionId = TargetQuestionId(route.RequestId, quotedMessageId) ??
            ExtensionString(route.ExtensionData, "questionId") ??
            (context.Input.Questions.Count == 1
                ? context.Input.Questions[0].Id
                : null);
        var question = context.Input.Questions.SingleOrDefault(item => string.Equals(
            item.Id,
            questionId,
            StringComparison.Ordinal));
        if (question is null)
        {
            await RespondAsync(
                intent,
                "无法确定引用卡片对应的问题，请使用卡片按钮回答。",
                cancellationToken);
            return true;
        }
        if (intent.Attachments is { Count: > 0 })
        {
            await RespondAsync(intent, "问题答案暂不支持附件。", cancellationToken);
            return true;
        }
        var answers = ParseAnswers(intent.Text, question);
        if (answers is null)
        {
            await RespondAsync(intent, AnswerUsage(question), cancellationToken);
            return true;
        }
        var selectionKey = route.ChatId;
        RegisterTarget(context, quotedMessageId, question.Id, selectionKey);
        var result = await RecordAnswerAsync(
            intent,
            context,
            question,
            answers,
            selectionKey,
            cancellationToken);
        await RespondAsync(intent, result.ToastContent, cancellationToken);
        return true;
    }

    private async Task<FeishuCallbackResult> DeferAsync(
        FeishuIntent intent,
        InputContext context,
        CancellationToken cancellationToken)
    {
        var claim = await stateOwner.TryClaimInputAsync(
            context.Input.RequestId,
            context.Input.SessionId,
            cancellationToken);
        if (claim is null)
        {
            return new("warning", "这组问题已经处理或正在处理中。");
        }
        var completed = false;
        var managedHookReleased = false;
        try
        {
            if (claim.Session.Runtime is RuntimeNames.Codex or RuntimeNames.ClaudeCode)
            {
                try
                {
                    await managedHooks.DeferInputToLocalAsync(
                        claim.Session.Runtime,
                        claim.Session.SessionId,
                        claim.Input.RequestId,
                        cancellationToken);
                    managedHookReleased = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return new("warning", "暂时无法转回电脑端，请稍后重试。");
                }
            }
            var deferred = await stateOwner.DeferClaimedInputAsync(
                context.Input.RequestId,
                context.Input.SessionId,
                managedHookReleased ? CancellationToken.None : cancellationToken);
            if (deferred is null)
            {
                return new("warning", "这组问题已经处理或失效。");
            }
            completed = true;
            var observed = Context(deferred.Input, deferred.Session, context.StoredSession);
            var deferredTargets = TargetsSnapshot(context.Input.RequestId);
            return await SynchronizeForCallbackAsync(
                intent,
                new(
                    "success",
                    "已转回电脑端回答。",
                    TerminalInputCard(observed, Parameter(intent, "questionId"))),
                async synchronizationCancellationToken =>
                {
                    await interactions.SynchronizeInputAsync(
                        deferred.Input,
                        observed.SessionView,
                        observed.Questions,
                        deferredTargets,
                        synchronizationCancellationToken);
                    ClearInteraction(context.Input.RequestId);
                },
                cancellationToken);
        }
        finally
        {
            if (!completed)
            {
                await stateOwner.ReleaseInputClaimAsync(
                    context.Input.RequestId,
                    CancellationToken.None);
            }
        }
    }
}
