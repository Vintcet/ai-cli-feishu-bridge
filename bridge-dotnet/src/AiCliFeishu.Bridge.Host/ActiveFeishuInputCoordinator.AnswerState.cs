using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuInputCoordinator
{
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
        var pendingTargets = TargetsSnapshot(context.Input.RequestId);
        var pendingSelections = SelectionsSnapshot(context.Input.RequestId);
        return SynchronizeForCallbackAsync(
            intent,
            new FeishuCallbackResult(
                "success",
                added
                    ? $"已选择“{Truncate(answer, 80)}”，可继续选择或提交。"
                    : $"已取消“{Truncate(answer, 80)}”的选择。",
                card),
            synchronizationCancellationToken =>
                interactions.SynchronizePendingInputAsync(
                    context.Input,
                    context.SessionView,
                    context.Questions,
                    pendingTargets,
                    pendingSelections,
                    intent.EventId,
                    synchronizationCancellationToken),
            cancellationToken);
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
            var questionIndex = current.Input.Questions
                .Select((item, index) => (item, index))
                .Single(item => string.Equals(
                    item.item.Id,
                    question.Id,
                    StringComparison.Ordinal))
                .index;
            var recordedTargets = TargetsSnapshot(context.Input.RequestId);
            return await SynchronizeForCallbackAsync(
                intent,
                new(
                    "success",
                    "已记录这道问题，请继续处理其他问题。",
                    renderer.RecordedInput(
                        current.SessionView,
                        current.Questions[questionIndex],
                        progress.Input.Answers[question.Id],
                        current.Questions.Count - progress.Input.Answers.Count,
                        questionIndex,
                        current.Questions.Count)),
                synchronizationCancellationToken =>
                    interactions.SynchronizeRecordedInputAsync(
                        progress.Input,
                        current.SessionView,
                        current.Questions,
                        recordedTargets,
                        question.Id,
                        intent.EventId,
                        synchronizationCancellationToken),
                cancellationToken);
        }

        if (!RuntimeReady(progress.Session))
        {
            return await FailedSubmissionAsync(
                context,
                progress,
                intent,
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
                intent,
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
                intent,
                "答案已发送，但暂时无法提交状态，请重试。",
                cancellationToken);
        }
        if (resolved is null)
        {
            return await FailedSubmissionAsync(
                context,
                progress,
                intent,
                "这组问题已经处理或失效。",
                cancellationToken);
        }
        var completed = Context(resolved.Input, resolved.Session, context.StoredSession);
        var completedTargets = TargetsSnapshot(context.Input.RequestId);
        return await SynchronizeForCallbackAsync(
            intent,
            new(
                "success",
                $"已把答案交给 {RuntimeDisplayName(progress.Session.Runtime)}。",
                TerminalInputCard(completed, question.Id)),
            async synchronizationCancellationToken =>
            {
                await interactions.SynchronizeInputAsync(
                    resolved.Input,
                    completed.SessionView,
                    completed.Questions,
                    completedTargets,
                    synchronizationCancellationToken);
                ClearInteraction(context.Input.RequestId);
            },
            cancellationToken);
    }

    private async Task<FeishuCallbackResult> FailedSubmissionAsync(
        InputContext original,
        BridgeInputAnswerProgress progress,
        FeishuIntent intent,
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
            var resolvedTargets = TargetsSnapshot(original.Input.RequestId);
            return await SynchronizeForCallbackAsync(
                intent,
                new(
                    "success",
                    $"已把答案交给 {RuntimeDisplayName(reset.Session.Runtime)}。",
                    TerminalInputCard(resolved, Parameter(intent, "questionId"))),
                async synchronizationCancellationToken =>
                {
                    await interactions.SynchronizeInputAsync(
                        reset.Input,
                        resolved.SessionView,
                        resolved.Questions,
                        resolvedTargets,
                        synchronizationCancellationToken);
                    ClearInteraction(original.Input.RequestId);
                },
                cancellationToken);
        }
        if (reset is not null && reset.Input.Status == InputRequestStatuses.Pending)
        {
            var pending = Context(reset.Input, reset.Session, original.StoredSession);
            var pendingTargets = TargetsSnapshot(original.Input.RequestId);
            var pendingSelections = SelectionsSnapshot(original.Input.RequestId);
            return await SynchronizeForCallbackAsync(
                intent,
                new("warning", warning),
                synchronizationCancellationToken =>
                    interactions.SynchronizePendingInputAsync(
                        reset.Input,
                        pending.SessionView,
                        pending.Questions,
                        pendingTargets,
                        pendingSelections,
                        intent.EventId,
                        synchronizationCancellationToken),
                cancellationToken);
        }
        return new("warning", warning);
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
}
