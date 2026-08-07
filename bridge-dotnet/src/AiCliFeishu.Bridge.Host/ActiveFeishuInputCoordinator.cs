using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuInputCoordinator(
    IBridgeActiveInputStateOwner stateOwner,
    IBridgeRuntimeCommandGateway runtimeCommands,
    FeishuInteractionCoordinator interactions,
    IFeishuCardRenderer renderer,
    IFeishuGateway gateway,
    IManagedHookResponseSink managedHooks,
    TimeProvider? timeProvider = null)
{
    private const int MaximumAnswerLength = 1_000;
    private const int MaximumAnswerCount = 50;
    private readonly object interactionSync = new();
    private readonly Dictionary<InputSelectionKey, string[]> selections = [];
    private readonly Dictionary<string, Dictionary<string, FeishuInputCardTarget>> targets =
        new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<FeishuCallbackResult> HandleAsync(
        FeishuIntent intent,
        NodeStoreSnapshot store,
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

        var questionId = Parameter(intent, "questionId");
        var selectionKey = SelectionKey(intent);
        if (selectionKey is null)
        {
            return new("warning", "这张问题卡已经失效。");
        }
        RegisterTarget(context, intent.MessageId, questionId, selectionKey);

        if (context.Input.Status != InputRequestStatuses.Pending)
        {
            await SynchronizeObservedAsync(context, requestId, cancellationToken);
            ClearInteraction(requestId);
            return new("warning", "这组问题已经处理或失效。");
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
        NodeStoreSnapshot store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(store);
        var parentMessageId = Parameter(intent, "parentMessageId");
        if (parentMessageId is null ||
            !store.Routes.Messages.TryGetValue(parentMessageId, out var route) ||
            !string.Equals(route.Kind, "input", StringComparison.Ordinal))
        {
            return false;
        }
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
        var questionId = TargetQuestionId(route.RequestId, parentMessageId) ??
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
        RegisterTarget(context, parentMessageId, question.Id, selectionKey);
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
            await interactions.SynchronizeInputAsync(
                deferred.Input,
                observed.SessionView,
                observed.Questions,
                TargetsSnapshot(context.Input.RequestId),
                cancellationToken);
            ClearInteraction(context.Input.RequestId);
            return new("success", "已转回电脑端回答。");
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

    private Task<FeishuCallbackResult> AnswerOptionAsync(
        FeishuIntent intent,
        InputContext context,
        string selectionKey,
        CancellationToken cancellationToken)
    {
        var question = Question(context, Parameter(intent, "questionId"));
        var answer = Parameter(intent, "answer");
        if (question is null || answer is null)
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "问题参数不完整。"));
        }
        if (question.Multiple)
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "多选问题需要先选择选项再提交。"));
        }
        if (!question.Options.Contains(answer, StringComparer.Ordinal))
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "这个答案不属于当前问题。"));
        }
        return RecordAnswerAsync(
            intent,
            context,
            question,
            [answer],
            selectionKey,
            cancellationToken);
    }

    private Task<FeishuCallbackResult> SubmitAsync(
        FeishuIntent intent,
        InputContext context,
        string selectionKey,
        CancellationToken cancellationToken)
    {
        var question = Question(context, Parameter(intent, "questionId"));
        if (question is null)
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "问题参数不完整。"));
        }
        if (!question.Multiple)
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "这不是多选问题。"));
        }
        var selected = SelectedAnswers(
            context.Input.RequestId,
            question.Id,
            selectionKey);
        return selected.Count == 0
            ? Task.FromResult(new FeishuCallbackResult(
                "error",
                "请至少选择一个选项。"))
            : RecordAnswerAsync(
                intent,
                context,
                question,
                selected,
                selectionKey,
                cancellationToken);
    }

    private Task<FeishuCallbackResult> ToggleAsync(
        FeishuIntent intent,
        InputContext context,
        string selectionKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var question = Question(context, Parameter(intent, "questionId"));
        var answer = Parameter(intent, "answer");
        if (question is null || answer is null || !question.Multiple)
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "多选答案参数不完整。"));
        }
        if (!question.Options.Contains(answer, StringComparer.Ordinal))
        {
            return Task.FromResult(new FeishuCallbackResult(
                "error",
                "这个答案不属于当前问题。"));
        }
        if (context.Input.Answers.ContainsKey(question.Id))
        {
            return Task.FromResult(new FeishuCallbackResult(
                "warning",
                "这道问题已经处理或失效。"));
        }
        string[] selected;
        bool added;
        lock (interactionSync)
        {
            var key = new InputSelectionKey(
                context.Input.RequestId,
                question.Id,
                selectionKey);
            selections.TryGetValue(key, out var existing);
            var values = existing?.ToList() ?? [];
            added = !values.Remove(answer);
            if (added)
            {
                values.Add(answer);
            }
            selected = values.ToArray();
            if (selected.Length == 0)
            {
                selections.Remove(key);
            }
            else
            {
                selections[key] = selected;
            }
        }
        var view = context.Questions.Single(item => string.Equals(
            item.Id,
            question.Id,
            StringComparison.Ordinal));
        var index = context.Input.Questions
            .Select((item, questionIndex) => (item, questionIndex))
            .Single(item => string.Equals(
                item.item.Id,
                question.Id,
                StringComparison.Ordinal))
            .questionIndex;
        var card = renderer.PendingInput(
            context.SessionView,
            context.Input.RequestId,
            view,
            index,
            context.Questions.Count,
            selected,
            selectionKey);
        return Task.FromResult(new FeishuCallbackResult(
            "success",
            added
                ? $"已选择“{Truncate(answer, 80)}”，可继续选择或提交。"
                : $"已取消“{Truncate(answer, 80)}”的选择。",
            card));
    }

    private async Task<FeishuCallbackResult> RecordAnswerAsync(
        FeishuIntent intent,
        InputContext context,
        InputQuestionState question,
        IReadOnlyList<string> answers,
        string selectionKey,
        CancellationToken cancellationToken)
    {
        BridgeInputAnswerProgress? progress;
        try
        {
            progress = await stateOwner.TryRecordInputAnswerAsync(
                context.Input.RequestId,
                context.Input.SessionId,
                question.Id,
                answers,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return new("error", "答案不符合当前问题的要求。");
        }
        if (progress is null)
        {
            return new("warning", "这道问题已经处理或失效。");
        }
        RemoveSelection(context.Input.RequestId, question.Id, selectionKey);
        var current = Context(progress.Input, progress.Session, context.StoredSession);
        if (!progress.Complete)
        {
            await interactions.SynchronizeRecordedInputAsync(
                progress.Input,
                current.SessionView,
                current.Questions,
                TargetsSnapshot(context.Input.RequestId),
                question.Id,
                intent.EventId,
                cancellationToken);
            return new("success", "已记录这道问题，请继续处理其他问题。");
        }

        if (!RuntimeReady(progress.Session))
        {
            return await FailedSubmissionAsync(
                context,
                progress,
                intent.EventId,
                "对应窗口尚未就绪，未提交这组答案。",
                cancellationToken);
        }
        try
        {
            await runtimeCommands.DispatchAsync(
                Command(intent, progress),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreMultipleSelections(progress.Input);
            await stateOwner.ResetClaimedInputAsync(
                context.Input.RequestId,
                context.Input.SessionId,
                CancellationToken.None);
            throw;
        }
        catch
        {
            return await FailedSubmissionAsync(
                context,
                progress,
                intent.EventId,
                "暂时无法提交，请稍后重试。",
                cancellationToken);
        }

        BridgeInputClaim? resolved;
        try
        {
            resolved = await stateOwner.ResolveClaimedInputAsync(
                context.Input.RequestId,
                context.Input.SessionId,
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await FailedSubmissionAsync(
                context,
                progress,
                intent.EventId,
                "答案已发送，但暂时无法提交状态，请重试。",
                cancellationToken);
        }
        if (resolved is null)
        {
            return await FailedSubmissionAsync(
                context,
                progress,
                intent.EventId,
                "这组问题已经处理或失效。",
                cancellationToken);
        }
        var completed = Context(resolved.Input, resolved.Session, context.StoredSession);
        await interactions.SynchronizeInputAsync(
            resolved.Input,
            completed.SessionView,
            completed.Questions,
            TargetsSnapshot(context.Input.RequestId),
            cancellationToken);
        ClearInteraction(context.Input.RequestId);
        return new(
            "success",
            $"已把答案交给 {RuntimeDisplayName(progress.Session.Runtime)}。");
    }

    private async Task<FeishuCallbackResult> FailedSubmissionAsync(
        InputContext original,
        BridgeInputAnswerProgress progress,
        string revisionToken,
        string warning,
        CancellationToken cancellationToken)
    {
        RestoreMultipleSelections(progress.Input);
        var reset = await stateOwner.ResetClaimedInputAsync(
            original.Input.RequestId,
            original.Input.SessionId,
            CancellationToken.None);
        if (reset is not null && reset.Input.Status == InputRequestStatuses.Resolved)
        {
            var resolved = Context(reset.Input, reset.Session, original.StoredSession);
            await interactions.SynchronizeInputAsync(
                reset.Input,
                resolved.SessionView,
                resolved.Questions,
                TargetsSnapshot(original.Input.RequestId),
                cancellationToken);
            ClearInteraction(original.Input.RequestId);
            return new(
                "success",
                $"已把答案交给 {RuntimeDisplayName(reset.Session.Runtime)}。");
        }
        if (reset is not null && reset.Input.Status == InputRequestStatuses.Pending)
        {
            var pending = Context(reset.Input, reset.Session, original.StoredSession);
            await interactions.SynchronizePendingInputAsync(
                reset.Input,
                pending.SessionView,
                pending.Questions,
                TargetsSnapshot(original.Input.RequestId),
                SelectionsSnapshot(original.Input.RequestId),
                revisionToken,
                cancellationToken);
        }
        return new("warning", warning);
    }

    private async Task SynchronizeObservedAsync(
        InputContext context,
        string requestId,
        CancellationToken cancellationToken)
    {
        await interactions.SynchronizeInputAsync(
            context.Input,
            context.SessionView,
            context.Questions,
            TargetsSnapshot(requestId),
            cancellationToken);
    }

    private bool RuntimeReady(SessionState session)
    {
        if (!RuntimeNames.All.Contains(session.Runtime))
        {
            return false;
        }
        try
        {
            return runtimeCommands.IsReady(
                session.Runtime,
                new RuntimeSession(session.SessionId, session.Cwd));
        }
        catch
        {
            return false;
        }
    }

    private static RuntimeCommandEnvelope Command(
        FeishuIntent intent,
        BridgeInputAnswerProgress progress) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = progress.Session.Runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = progress.Session.SessionId,
                Cwd = progress.Session.Cwd,
            },
            TraceId = intent.TraceId,
            CorrelationId = intent.EventId,
            CommandId = $"feishu-input-{progress.Input.RequestId}",
            CommandType = RuntimeCommandTypes.InputResolve,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Payload = JsonSerializer.SerializeToElement(new
            {
                requestId = progress.Input.RequestId,
                answers = progress.Input.Answers.ToDictionary(
                    item => item.Key,
                    item => item.Value.ToArray(),
                    StringComparer.Ordinal),
            }),
        };

    private InputContext? TryContext(
        string requestId,
        string sessionId,
        NodeStoreSnapshot store)
    {
        var current = stateOwner.Snapshot;
        if (!current.Initialized)
        {
            throw new InvalidOperationException("Active Host 业务状态尚未初始化。");
        }
        if (!current.Inputs.Requests.TryGetValue(requestId, out var input) ||
            !string.Equals(input.SessionId, sessionId, StringComparison.Ordinal) ||
            !current.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !store.Sessions.Sessions.TryGetValue(sessionId, out var stored) ||
            !string.Equals(session.Runtime, Runtime(stored), StringComparison.Ordinal) ||
            !string.Equals(session.Cwd, stored.Cwd, StringComparison.Ordinal))
        {
            return null;
        }
        return Context(input, session, stored);
    }

    private static InputContext Context(
        InputRequestState input,
        SessionState session,
        SessionStoreRecord stored)
    {
        var questions = input.Questions.Select((question, index) => new FeishuInputQuestionView(
            question.Id,
            string.IsNullOrWhiteSpace(question.Header)
                ? $"问题 {index + 1}"
                : question.Header,
            string.IsNullOrWhiteSpace(question.Prompt)
                ? question.Id
                : question.Prompt,
            question.Multiple,
            question.AllowsCustom,
            question.IsSecret,
            question.Options)).ToArray();
        return new(
            input,
            session,
            stored,
            questions,
            new(
                session.SessionId,
                session.Runtime,
                ExtensionString(stored.ExtensionData, "alias") ??
                    stored.ProjectName ??
                    stored.ShortId ??
                    ShortId(stored.SessionId),
                session.Cwd));
    }

    private static InputQuestionState? Question(InputContext context, string? questionId) =>
        questionId is null
            ? null
            : context.Input.Questions.SingleOrDefault(item => string.Equals(
                item.Id,
                questionId,
                StringComparison.Ordinal));

    private void RegisterTarget(
        InputContext context,
        string messageId,
        string? questionId,
        string selectionKey)
    {
        if (string.IsNullOrWhiteSpace(messageId) || questionId is null)
        {
            return;
        }
        var index = context.Input.Questions
            .Select((question, questionIndex) => (question, questionIndex))
            .SingleOrDefault(item => string.Equals(
                item.question.Id,
                questionId,
                StringComparison.Ordinal));
        if (index.question is null)
        {
            return;
        }
        lock (interactionSync)
        {
            if (!targets.TryGetValue(context.Input.RequestId, out var requestTargets))
            {
                requestTargets = new(StringComparer.Ordinal);
                targets.Add(context.Input.RequestId, requestTargets);
            }
            requestTargets[messageId] = new(
                messageId,
                questionId,
                index.questionIndex,
                selectionKey);
        }
    }

    private FeishuInputCardTarget[] TargetsSnapshot(string requestId)
    {
        lock (interactionSync)
        {
            return targets.TryGetValue(requestId, out var requestTargets)
                ? requestTargets.Values
                    .OrderBy(target => target.QuestionIndex)
                    .ThenBy(target => target.MessageId, StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
    }

    private string? TargetQuestionId(string requestId, string messageId)
    {
        lock (interactionSync)
        {
            return targets.TryGetValue(requestId, out var requestTargets) &&
                requestTargets.TryGetValue(messageId, out var target)
                    ? target.QuestionId
                    : null;
        }
    }

    private IReadOnlyList<string> SelectedAnswers(
        string requestId,
        string questionId,
        string selectionKey)
    {
        lock (interactionSync)
        {
            return selections.TryGetValue(
                    new(requestId, questionId, selectionKey),
                    out var selected)
                ? selected.ToArray()
                : [];
        }
    }

    private void RemoveSelection(string requestId, string questionId, string selectionKey)
    {
        lock (interactionSync)
        {
            selections.Remove(new(requestId, questionId, selectionKey));
        }
    }

    private void RestoreMultipleSelections(InputRequestState input)
    {
        lock (interactionSync)
        {
            if (!targets.TryGetValue(input.RequestId, out var requestTargets))
            {
                return;
            }
            foreach (var target in requestTargets.Values)
            {
                var question = input.Questions.SingleOrDefault(item => string.Equals(
                    item.Id,
                    target.QuestionId,
                    StringComparison.Ordinal));
                if (question?.Multiple != true || target.SelectionKey is null ||
                    !input.Answers.TryGetValue(question.Id, out var answers))
                {
                    continue;
                }
                selections[new(input.RequestId, question.Id, target.SelectionKey)] =
                    answers.ToArray();
            }
        }
    }

    private IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, IReadOnlyList<string>>> SelectionsSnapshot(
            string requestId)
    {
        lock (interactionSync)
        {
            return selections
                .Where(item => string.Equals(
                    item.Key.RequestId,
                    requestId,
                    StringComparison.Ordinal))
                .GroupBy(item => item.Key.SelectionKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<string, IReadOnlyList<string>>)group
                        .ToDictionary(
                            item => item.Key.QuestionId,
                            item => (IReadOnlyList<string>)item.Value.ToArray(),
                            StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }
    }

    private void ClearInteraction(string requestId)
    {
        lock (interactionSync)
        {
            targets.Remove(requestId);
            foreach (var key in selections.Keys.Where(key => string.Equals(
                         key.RequestId,
                         requestId,
                         StringComparison.Ordinal)).ToArray())
            {
                selections.Remove(key);
            }
        }
    }

    private static IReadOnlyList<string>? ParseAnswers(
        string? text,
        InputQuestionState question)
    {
        var raw = text?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumAnswerLength)
        {
            return null;
        }
        var exact = MatchOption(raw, question.Options);
        if (exact is not null)
        {
            return [exact];
        }
        if (!question.Multiple)
        {
            return question.AllowsCustom ? [raw] : null;
        }
        var values = raw.Split(
                [',', '，', '、', '+', '\n'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length is 0 or > MaximumAnswerCount)
        {
            return null;
        }
        var answers = new List<string>(values.Length);
        foreach (var value in values)
        {
            var option = MatchOption(value, question.Options);
            if (option is null && !question.AllowsCustom)
            {
                return null;
            }
            answers.Add(option ?? value);
        }
        return answers;
    }

    private static string? MatchOption(string value, IReadOnlyList<string> options)
    {
        if (int.TryParse(value, out var number) &&
            number >= 1 && number <= options.Count &&
            number.ToString() == value)
        {
            return options[number - 1];
        }
        return options.FirstOrDefault(option => string.Equals(
            option,
            value,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string AnswerUsage(InputQuestionState question) =>
        question.Multiple
            ? "答案格式不正确。多选题请用逗号分隔选项编号或文字。"
            : question.Options.Count > 0
                ? "答案格式不正确。请回复选项编号或文字。"
                : "答案不能为空。";

    private static string? SelectionKey(FeishuIntent intent)
    {
        var supplied = Parameter(intent, "selectionKey");
        return supplied is null
            ? $"operator:{intent.OperatorOpenId}"
            : string.Equals(supplied, intent.ChatId, StringComparison.Ordinal)
                ? supplied
                : null;
    }

    private async Task RespondAsync(
        FeishuIntent intent,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await gateway.ReplyTextAsync(intent.MessageId, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _ = await gateway.SendTextAsync(intent.ChatId, text, cancellationToken);
        }
    }

    private static string? Parameter(FeishuIntent intent, string name) =>
        intent.Parameters?.TryGetValue(name, out var value) == true &&
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Any(char.IsControl)
            ? value.Trim()
            : null;

    private static string? ExtensionString(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;

    private static string ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }

    private static string RuntimeDisplayName(string runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : $"{value[..Math.Max(0, length - 1)]}…";

    private sealed record InputContext(
        InputRequestState Input,
        SessionState Session,
        SessionStoreRecord StoredSession,
        IReadOnlyList<FeishuInputQuestionView> Questions,
        FeishuSessionView SessionView);

    private readonly record struct InputSelectionKey(
        string RequestId,
        string QuestionId,
        string SelectionKey);
}
