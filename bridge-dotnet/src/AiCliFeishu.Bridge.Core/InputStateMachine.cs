namespace AiCliFeishu.Bridge.Core;

public static class InputRequestStatuses
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Local = "local";
    public const string TimedOut = "timeout";
}

public sealed record InputQuestionState(
    string Id,
    bool Multiple,
    bool AllowsCustom,
    IReadOnlyList<string> Options,
    string? Header = null,
    string? Prompt = null,
    bool IsSecret = false);

public sealed record InputRequestState(
    string RequestId,
    string SessionId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<InputQuestionState> Questions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Answers,
    DateTimeOffset? ResolvedAt = null);

public sealed record InputRegistryState(
    IReadOnlyDictionary<string, InputRequestState> Requests)
{
    public static InputRegistryState Empty { get; } = new(
        new Dictionary<string, InputRequestState>(StringComparer.Ordinal));
}

public static class InputStateMachine
{
    public static InputRegistryState Create(
        InputRegistryState state,
        InputRequestState request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Status != InputRequestStatuses.Pending ||
            request.ExpiresAt <= request.CreatedAt ||
            request.Questions.Count == 0 ||
            request.Questions.Any(item => string.IsNullOrWhiteSpace(item.Id)) ||
            request.Questions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() !=
                request.Questions.Count)
        {
            throw new ArgumentException("补充问题必须是具有有效问题和期限的 pending 请求。", nameof(request));
        }
        if (state.Requests.ContainsKey(request.RequestId))
        {
            throw new InvalidOperationException($"补充问题 {request.RequestId} 已存在。 ");
        }
        var requests = Copy(state.Requests);
        requests.Add(request.RequestId, request with
        {
            Questions = request.Questions.Select(item => item with
            {
                Options = item.Options.ToArray(),
            }).ToArray(),
            Answers = CloneAnswers(request.Answers),
        });
        return state with { Requests = requests };
    }

    public static StateTransition<InputRegistryState, bool> Answer(
        InputRegistryState state,
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        DateTimeOffset resolvedAt)
    {
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != InputRequestStatuses.Pending)
        {
            return new(state, false);
        }
        ValidateAnswers(request, answers);
        return Resolve(state, request, InputRequestStatuses.Resolved, answers, resolvedAt);
    }

    public static StateTransition<InputRegistryState, bool> RecordAnswer(
        InputRegistryState state,
        string requestId,
        string questionId,
        IReadOnlyList<string> answers)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentNullException.ThrowIfNull(answers);
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != InputRequestStatuses.Pending ||
            request.Answers.ContainsKey(questionId))
        {
            return new(state, false);
        }
        var question = request.Questions.SingleOrDefault(item =>
            string.Equals(item.Id, questionId, StringComparison.Ordinal));
        if (question is null)
        {
            return new(state, false);
        }
        ValidateQuestionAnswer(question, answers);
        var recorded = CloneAnswers(request.Answers);
        recorded.Add(questionId, answers.ToArray());
        var requests = Copy(state.Requests);
        requests[requestId] = request with { Answers = recorded };
        return new(state with { Requests = requests }, true);
    }

    public static StateTransition<InputRegistryState, bool> ClearAnswers(
        InputRegistryState state,
        string requestId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != InputRequestStatuses.Pending ||
            request.Answers.Count == 0)
        {
            return new(state, false);
        }
        var requests = Copy(state.Requests);
        requests[requestId] = request with
        {
            Answers = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.Ordinal),
        };
        return new(state with { Requests = requests }, true);
    }

    public static bool HasCompleteAnswers(InputRequestState request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Questions.Count > 0 &&
            request.Questions.All(question => request.Answers.ContainsKey(question.Id));
    }

    public static StateTransition<InputRegistryState, bool> ResolveExternally(
        InputRegistryState state,
        string requestId,
        DateTimeOffset resolvedAt)
    {
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != InputRequestStatuses.Pending)
        {
            return new(state, false);
        }
        return Resolve(
            state,
            request,
            InputRequestStatuses.Local,
            request.Answers,
            resolvedAt);
    }

    public static StateTransition<InputRegistryState, bool> Expire(
        InputRegistryState state,
        string requestId,
        DateTimeOffset occurredAt)
    {
        if (!state.Requests.TryGetValue(requestId, out var request) ||
            request.Status != InputRequestStatuses.Pending ||
            occurredAt < request.ExpiresAt)
        {
            return new(state, false);
        }
        return Resolve(
            state,
            request,
            InputRequestStatuses.TimedOut,
            request.Answers,
            occurredAt);
    }

    private static StateTransition<InputRegistryState, bool> Resolve(
        InputRegistryState state,
        InputRequestState request,
        string status,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        DateTimeOffset resolvedAt)
    {
        if (resolvedAt < request.CreatedAt)
        {
            throw new InvalidOperationException("补充问题完成时间不能早于创建时间。 ");
        }
        var requests = Copy(state.Requests);
        requests[request.RequestId] = request with
        {
            Status = status,
            Answers = CloneAnswers(answers),
            ResolvedAt = resolvedAt,
        };
        return new(state with { Requests = requests }, true);
    }

    private static void ValidateAnswers(
        InputRequestState request,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var expected = request.Questions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(answers.Keys))
        {
            throw new ArgumentException("答案必须完整覆盖本次补充问题。", nameof(answers));
        }
        foreach (var question in request.Questions)
        {
            ValidateQuestionAnswer(question, answers[question.Id]);
        }
    }

    private static void ValidateQuestionAnswer(
        InputQuestionState question,
        IReadOnlyList<string> answers)
    {
        if (answers.Count == 0 || (!question.Multiple && answers.Count != 1))
        {
            throw new ArgumentException($"问题 {question.Id} 的答案数量不正确。", nameof(answers));
        }
        if (answers.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"问题 {question.Id} 的答案不能为空。", nameof(answers));
        }
        if (!question.AllowsCustom && answers.Any(value =>
                !question.Options.Contains(value, StringComparer.Ordinal)))
        {
            throw new ArgumentException($"问题 {question.Id} 不接受自定义答案。", nameof(answers));
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> CloneAnswers(
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
        answers.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.ToArray(),
            StringComparer.Ordinal);

    private static Dictionary<string, InputRequestState> Copy(
        IReadOnlyDictionary<string, InputRequestState> source) =>
        new(source, StringComparer.Ordinal);
}
