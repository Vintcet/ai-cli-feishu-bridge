using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuInputCoordinator
{
    private FeishuCardView? TerminalInputCard(
        InputContext context,
        string? questionId)
    {
        var resolution = context.Input.Status switch
        {
            InputRequestStatuses.Resolved => "answered",
            InputRequestStatuses.Local => "local",
            InputRequestStatuses.TimedOut => "timeout",
            _ => null,
        };
        if (resolution is null)
        {
            return null;
        }
        var questionIndex = context.Input.Questions
            .Select((question, index) => (question, index))
            .FirstOrDefault(item => string.Equals(
                item.question.Id,
                questionId,
                StringComparison.Ordinal));
        if (questionIndex.question is null && context.Input.Questions.Count == 1)
        {
            questionIndex = (context.Input.Questions[0], 0);
        }
        if (questionIndex.question is null)
        {
            return null;
        }
        context.Input.Answers.TryGetValue(questionIndex.question.Id, out var answers);
        return renderer.ResolvedInput(
            context.SessionView,
            context.Questions[questionIndex.index],
            answers,
            resolution,
            questionIndex.index,
            context.Questions.Count);
    }

    private static async Task<FeishuCallbackResult> SynchronizeForCallbackAsync(
        FeishuIntent intent,
        FeishuCallbackResult result,
        Func<CancellationToken, Task> synchronize,
        CancellationToken cancellationToken)
    {
        if (!IsCardAction(intent))
        {
            await synchronize(cancellationToken);
            return result;
        }

        var existing = result.AfterAcknowledged;
        return result with
        {
            Card = null,
            AfterAcknowledged = async acknowledgedCancellationToken =>
            {
                if (existing is not null)
                {
                    await existing(acknowledgedCancellationToken);
                }
                await synchronize(acknowledgedCancellationToken);
            },
        };
    }

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

    private void RestoreTargets(InputContext context, BridgeStoreSnapshot store)
    {
        var questionIds = context.Input.Questions
            .Select(question => question.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var route in store.Routes.Messages.Values.Where(route =>
                     string.Equals(route.Kind, "input", StringComparison.Ordinal) &&
                     string.Equals(route.RequestId, context.Input.RequestId, StringComparison.Ordinal) &&
                     string.Equals(route.SessionId, context.Input.SessionId, StringComparison.Ordinal)))
        {
            var questionId = ExtensionString(route.ExtensionData, "questionId") ??
                (context.Input.Questions.Count == 1
                    ? context.Input.Questions[0].Id
                    : null);
            if (questionId is null || !questionIds.Contains(questionId))
            {
                continue;
            }
            var selectionKey = ExtensionString(route.ExtensionData, "selectionKey") ??
                route.ChatId;
            RegisterTarget(context, route.MessageId, questionId, selectionKey);
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
}
