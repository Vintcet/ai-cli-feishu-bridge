using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public sealed class FeishuInteractionCoordinator(
    IFeishuGateway gateway,
    IFeishuCardRenderer renderer,
    IFeishuCardPatchLedger patchLedger)
{
    public async Task<StateTransition<ApprovalRegistryState, bool>> ResolveApprovalAsync(
        ApprovalRegistryState state,
        string requestId,
        string resolution,
        DateTimeOffset resolvedAt,
        FeishuSessionView session,
        FeishuApprovalView view,
        CancellationToken cancellationToken = default)
    {
        var transition = ApprovalStateMachine.ResolveExternally(
            state,
            requestId,
            resolution,
            resolvedAt);
        if (transition.Value)
        {
            await SynchronizeApprovalAsync(
                transition.State.Requests[requestId],
                session,
                view,
                cancellationToken);
        }
        return transition;
    }

    public async Task SynchronizeApprovalAsync(
        ApprovalState approval,
        FeishuSessionView session,
        FeishuApprovalView view,
        CancellationToken cancellationToken = default)
    {
        if (approval.Status != ApprovalStatuses.Resolved || approval.Resolution is null)
        {
            return;
        }
        var card = renderer.ResolvedApproval(session, view, approval.Resolution);
        var revision = $"approval:{approval.RequestId}:{approval.Resolution}:{approval.ResolvedAt:O}";
        await PatchAllAsync(approval.MessageIds, revision, card, cancellationToken);
    }

    public Task SynchronizeDeferredApprovalAsync(
        ApprovalState approval,
        FeishuSessionView session,
        FeishuApprovalView view,
        CancellationToken cancellationToken = default)
    {
        if (approval.Status != ApprovalStatuses.Pending)
        {
            return Task.CompletedTask;
        }
        var card = renderer.DeferredApproval(session, view);
        var revision = $"approval:{approval.RequestId}:desktop";
        return PatchAllAsync(approval.MessageIds, revision, card, cancellationToken);
    }

    public async Task<StateTransition<InputRegistryState, bool>> ResolveInputLocallyAsync(
        InputRegistryState state,
        string requestId,
        DateTimeOffset resolvedAt,
        FeishuSessionView session,
        IReadOnlyList<FeishuInputQuestionView> questions,
        IReadOnlyList<FeishuInputCardTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var transition = InputStateMachine.ResolveExternally(state, requestId, resolvedAt);
        if (transition.Value)
        {
            await SynchronizeInputAsync(
                transition.State.Requests[requestId],
                session,
                questions,
                targets,
                cancellationToken);
        }
        return transition;
    }

    public async Task SynchronizeInputAsync(
        InputRequestState request,
        FeishuSessionView session,
        IReadOnlyList<FeishuInputQuestionView> questions,
        IReadOnlyList<FeishuInputCardTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var resolution = request.Status switch
        {
            InputRequestStatuses.Resolved => "answered",
            InputRequestStatuses.Local => "local",
            InputRequestStatuses.TimedOut => "timeout",
            _ => null,
        };
        if (resolution is null)
        {
            return;
        }
        var views = questions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!views.TryGetValue(target.QuestionId, out var question) ||
                target.QuestionIndex < 0 || target.QuestionIndex >= questions.Count)
            {
                continue;
            }
            var revision =
                $"input:{request.RequestId}:{target.QuestionId}:{resolution}:{request.ResolvedAt:O}";
            if (!patchLedger.TryClaim(target.MessageId, revision))
            {
                continue;
            }
            try
            {
                request.Answers.TryGetValue(target.QuestionId, out var answers);
                var card = renderer.ResolvedInput(
                    session,
                    question,
                    answers,
                    resolution,
                    target.QuestionIndex,
                    questions.Count);
                await gateway.PatchCardAsync(target.MessageId, card, cancellationToken);
            }
            catch
            {
                patchLedger.Release(target.MessageId, revision);
                throw;
            }
        }
    }

    public async Task SynchronizeRecordedInputAsync(
        InputRequestState request,
        FeishuSessionView session,
        IReadOnlyList<FeishuInputQuestionView> questions,
        IReadOnlyList<FeishuInputCardTarget> targets,
        string questionId,
        string revisionToken,
        CancellationToken cancellationToken = default)
    {
        if (request.Status != InputRequestStatuses.Pending ||
            !request.Answers.TryGetValue(questionId, out var answers))
        {
            return;
        }
        var questionIndex = questions
            .Select((question, index) => (question, index))
            .SingleOrDefault(item => string.Equals(
                item.question.Id,
                questionId,
                StringComparison.Ordinal));
        if (questionIndex.question is null)
        {
            return;
        }
        var remaining = questions.Count - request.Answers.Count;
        if (remaining < 1)
        {
            return;
        }
        var card = renderer.RecordedInput(
            session,
            questionIndex.question,
            answers,
            remaining,
            questionIndex.index,
            questions.Count);
        foreach (var target in targets.Where(target => string.Equals(
                     target.QuestionId,
                     questionId,
                     StringComparison.Ordinal)))
        {
            await PatchInputCardAsync(
                target.MessageId,
                $"input:{request.RequestId}:{questionId}:recorded:{revisionToken}",
                card,
                cancellationToken);
        }
    }

    public async Task SynchronizePendingInputAsync(
        InputRequestState request,
        FeishuSessionView session,
        IReadOnlyList<FeishuInputQuestionView> questions,
        IReadOnlyList<FeishuInputCardTarget> targets,
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, IReadOnlyList<string>>> selections,
        string revisionToken,
        CancellationToken cancellationToken = default)
    {
        if (request.Status != InputRequestStatuses.Pending)
        {
            return;
        }
        var views = questions
            .Select((question, index) => (question, index))
            .ToDictionary(item => item.question.Id, StringComparer.Ordinal);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!views.TryGetValue(target.QuestionId, out var view))
            {
                continue;
            }
            IReadOnlyList<string>? selected = null;
            if (target.SelectionKey is not null &&
                selections.TryGetValue(target.SelectionKey, out var scoped))
            {
                scoped.TryGetValue(target.QuestionId, out selected);
            }
            var card = renderer.PendingInput(
                session,
                request.RequestId,
                view.question,
                view.index,
                questions.Count,
                selected,
                target.SelectionKey);
            await PatchInputCardAsync(
                target.MessageId,
                $"input:{request.RequestId}:{target.QuestionId}:pending:{revisionToken}",
                card,
                cancellationToken);
        }
    }

    private async Task PatchAllAsync(
        IReadOnlyList<string> messageIds,
        string revision,
        FeishuCardView card,
        CancellationToken cancellationToken)
    {
        foreach (var messageId in messageIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!patchLedger.TryClaim(messageId, revision))
            {
                continue;
            }
            try
            {
                await gateway.PatchCardAsync(messageId, card, cancellationToken);
            }
            catch
            {
                patchLedger.Release(messageId, revision);
                throw;
            }
        }
    }

    private async Task PatchInputCardAsync(
        string messageId,
        string revision,
        FeishuCardView card,
        CancellationToken cancellationToken)
    {
        if (!patchLedger.TryClaim(messageId, revision))
        {
            return;
        }
        try
        {
            await gateway.PatchCardAsync(messageId, card, cancellationToken);
        }
        catch
        {
            patchLedger.Release(messageId, revision);
            throw;
        }
    }
}
