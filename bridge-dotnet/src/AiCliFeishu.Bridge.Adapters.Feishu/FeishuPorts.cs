namespace AiCliFeishu.Bridge.Adapters.Feishu;

public interface IFeishuEventSource
{
    IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

public interface IFeishuIntentSink
{
    Task<FeishuCallbackResult?> PublishAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken = default);
}

public interface IFeishuGateway
{
    Task<string> SendTextAsync(
        string chatId,
        string text,
        CancellationToken cancellationToken = default);

    Task<string> ReplyTextAsync(
        string messageId,
        string text,
        CancellationToken cancellationToken = default);

    Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        CancellationToken cancellationToken = default);

    Task<FeishuSessionGroup> CreateSessionGroupAsync(
        string ownerOpenId,
        string name,
        string description,
        CancellationToken cancellationToken = default);

    Task UpdateSessionGroupNameAsync(
        string chatId,
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteSessionGroupAsync(
        string chatId,
        CancellationToken cancellationToken = default);

    Task<long> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default);

    Task<string> SendLocalFileAsync(
        string chatId,
        string filePath,
        CancellationToken cancellationToken = default);
}

public interface IFeishuCardRenderer
{
    FeishuCardView CommandMenu();

    FeishuCardView RuntimeSelection(
        string? workspaceRoot,
        FeishuRuntimeNewContext context);

    FeishuCardView RuntimeProjectForm(
        string runtime,
        string? workspaceRoot,
        FeishuRuntimeNewContext context);

    FeishuCardView RuntimeLaunchSubmitted(
        string runtime,
        string projectName,
        string workspaceRoot);

    FeishuCardView RuntimeLaunchCancelled(string runtime);

    FeishuCardView PendingApproval(
        FeishuSessionView session,
        FeishuApprovalView approval);

    FeishuCardView ResolvedApproval(
        FeishuSessionView session,
        FeishuApprovalView approval,
        string resolution,
        string status);

    FeishuCardView DeferredApproval(
        FeishuSessionView session,
        FeishuApprovalView approval);

    FeishuCardView PendingInput(
        FeishuSessionView session,
        string requestId,
        FeishuInputQuestionView question,
        int questionIndex,
        int questionCount,
        IReadOnlyList<string>? selectedAnswers = null,
        string? selectionKey = null);

    FeishuCardView RecordedInput(
        FeishuSessionView session,
        FeishuInputQuestionView question,
        IReadOnlyList<string> answers,
        int remainingQuestions,
        int questionIndex,
        int questionCount);

    FeishuCardView ResolvedInput(
        FeishuSessionView session,
        FeishuInputQuestionView question,
        IReadOnlyList<string>? answers,
        string resolution,
        int questionIndex,
        int questionCount);

    IReadOnlyList<FeishuCardView> RuntimeError(
        FeishuSessionView session,
        string error,
        FeishuRuntimeRetryView? retry = null);

    IReadOnlyList<FeishuCardView> RuntimeCompletion(
        FeishuSessionView session,
        string message);

    FeishuCardView RuntimeActivity(
        FeishuSessionView session,
        IReadOnlyList<FeishuActivityEventView> events,
        string startedAt,
        bool completed = false);
}

public interface IFeishuCardPatchLedger
{
    bool TryClaim(string messageId, string revision);

    void Release(string messageId, string revision);
}

public interface IFeishuInboundDeduplicator
{
    bool TryClaim(string eventId);

    void Release(string eventId);
}

public sealed class InMemoryFeishuInboundDeduplicator(int capacity = 2_048)
    : IFeishuInboundDeduplicator
{
    private readonly InMemoryFeishuClaimSet claims = new(capacity);

    public bool TryClaim(string eventId) => claims.TryClaim(eventId);

    public void Release(string eventId) => claims.Release(eventId);
}

public sealed class InMemoryFeishuCardPatchLedger(int capacity = 4_096)
    : IFeishuCardPatchLedger
{
    private readonly InMemoryFeishuClaimSet claims = new(capacity);

    public bool TryClaim(string messageId, string revision)
        => claims.TryClaim(Key(messageId, revision));

    public void Release(string messageId, string revision)
        => claims.Release(Key(messageId, revision));

    private static string Key(string messageId, string revision) =>
        $"{messageId}\n{revision}";
}

internal sealed class InMemoryFeishuClaimSet(int capacity)
{
    private readonly int boundedCapacity = Math.Max(1, capacity);
    private readonly LinkedList<string> order = new();
    private readonly Dictionary<string, LinkedListNode<string>> known =
        new(StringComparer.Ordinal);
    private readonly object sync = new();

    public bool TryClaim(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        lock (sync)
        {
            if (known.ContainsKey(key))
            {
                return false;
            }
            known.Add(key, order.AddLast(key));
            while (order.Count > boundedCapacity)
            {
                var oldest = order.First!;
                order.RemoveFirst();
                known.Remove(oldest.Value);
            }
            return true;
        }
    }

    public void Release(string key)
    {
        lock (sync)
        {
            if (known.Remove(key, out var node))
            {
                order.Remove(node);
            }
        }
    }
}
