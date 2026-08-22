using System.Text.Json;
using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class ManagedRuntimeHookBridge(
    ManagedRuntimeHookNormalizer normalizer,
    IRuntimeEventSink eventSink,
    int completedInteractionCapacity = 1_024,
    int activityQueueCapacity = 4,
    TimeSpan? activityDispatchTimeout = null) : IManagedHookResponseSink, IDisposable
{
    private static readonly JsonElement EmptyResponse =
        JsonSerializer.SerializeToElement(new { });
    private readonly object interactionLock = new();
    private readonly Dictionary<InteractionKey, PendingInteraction> pending = [];
    private readonly Dictionary<InteractionKey, CompletedInteraction> completed = [];
    private readonly Queue<InteractionKey> completedOrder = new();
    private readonly int capacity = Math.Max(1, completedInteractionCapacity);
    private readonly int activityCapacity = Math.Max(1, activityQueueCapacity);
    private readonly TimeSpan activityTimeout = PositiveTimeout(
        activityDispatchTimeout ?? TimeSpan.FromSeconds(3),
        nameof(activityDispatchTimeout));
    private readonly CancellationTokenSource activityLifetime = new();
    private readonly object activityQueueLock = new();
    private readonly Dictionary<ActivityQueueKey, ActivitySessionQueue> activityQueues = [];

    public bool IsReady(string runtime, string sessionExternalId)
    {
        if (string.IsNullOrWhiteSpace(runtime) ||
            string.IsNullOrWhiteSpace(sessionExternalId))
        {
            return false;
        }
        lock (interactionLock)
        {
            return pending.Keys.Any(key =>
                string.Equals(key.Runtime, runtime, StringComparison.Ordinal) &&
                string.Equals(
                    key.SessionId,
                    sessionExternalId,
                    StringComparison.Ordinal));
        }
    }

    public async Task<JsonElement> HandleAsync(
        JsonElement hook,
        string traceId,
        CancellationToken cancellationToken = default)
    {
        await FlushActivitiesAsync(hook, cancellationToken);
        var descriptor = DescribeInteraction(hook);
        if (descriptor is null)
        {
            var runtimeEvent = normalizer.Normalize(hook, traceId);
            if (runtimeEvent is not null)
            {
                try
                {
                    await eventSink.PublishAsync(runtimeEvent, cancellationToken);
                }
                catch
                {
                    normalizer.Release(hook);
                    throw;
                }
            }
            return EmptyResponse.Clone();
        }

        PendingInteraction interaction;
        var owner = false;
        lock (interactionLock)
        {
            if (completed.TryGetValue(descriptor.Value.Key, out var response))
            {
                return response.Response.Clone();
            }
            if (!pending.TryGetValue(descriptor.Value.Key, out interaction!))
            {
                interaction = new(descriptor.Value.Kind, hook.Clone());
                pending.Add(descriptor.Value.Key, interaction);
                owner = true;
            }
            interaction.WaiterCount++;
        }

        if (owner)
        {
            try
            {
                // Interactive hooks are deduplicated by their stable request key here so
                // repeated HTTP requests can receive the cached CLI response as well.
                var runtimeEvent = normalizer.Normalize(hook, traceId, deduplicate: false) ??
                    throw new InvalidDataException("交互 Hook 无法转换为标准事件。");
                await eventSink.PublishAsync(runtimeEvent, cancellationToken);
            }
            catch (Exception error)
            {
                Fail(descriptor.Value.Key, interaction, error);
                throw;
            }
        }

        try
        {
            return (await interaction.Completion.Task.WaitAsync(cancellationToken)).Clone();
        }
        finally
        {
            ReleaseWaiter(descriptor.Value.Key, interaction);
        }
    }

    public JsonElement EnqueueActivity(JsonElement hook, string traceId)
    {
        if (hook.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("活动 Hook 必须是 JSON 对象。");
        }
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new ArgumentException("Hook trace ID 不能为空。", nameof(traceId));
        }
        ObjectDisposedException.ThrowIf(activityLifetime.IsCancellationRequested, this);

        var runtimeEvent = normalizer.Normalize(hook, traceId);
        if (runtimeEvent is null)
        {
            return EmptyResponse.Clone();
        }
        if (runtimeEvent.EventType is not (
                RuntimeEventTypes.TurnStarted or
                RuntimeEventTypes.TurnActivity or
                RuntimeEventTypes.TurnFailed))
        {
            normalizer.Release(hook);
            throw new InvalidDataException("只有进度类 Hook 可以进入后台活动队列。");
        }

        var work = new QueuedActivity(runtimeEvent, hook.Clone());
        var key = new ActivityQueueKey(
            runtimeEvent.Runtime,
            runtimeEvent.Session!.ExternalId);
        ActivitySessionQueue queue;
        lock (activityQueueLock)
        {
            if (!activityQueues.TryGetValue(key, out queue!))
            {
                queue = new ActivitySessionQueue(
                    activityCapacity,
                    ProcessActivityAsync,
                    ReleaseActivity);
                activityQueues.Add(key, queue);
            }
        }
        if (!queue.Enqueue(work))
        {
            ReleaseActivity(work);
        }
        return EmptyResponse.Clone();
    }

    public Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var response = decision switch
        {
            "allow_once" or "allow_session" => JsonSerializer.SerializeToElement(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PermissionRequest",
                    decision = new { behavior = "allow" },
                },
            }),
            "deny" => JsonSerializer.SerializeToElement(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PermissionRequest",
                    decision = new
                    {
                        behavior = "deny",
                        message = "用户已通过飞书拒绝这次操作。",
                    },
                },
            }),
            _ => throw new InvalidDataException($"未知的审批决定 {decision}。"),
        };
        Complete(
            new(InteractionKind.Approval, runtime, sessionExternalId, requestId),
            response,
            $"approval:{decision}");
        return Task.CompletedTask;
    }

    public Task ResolveInputAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new InteractionKey(
            InteractionKind.Input,
            runtime,
            sessionExternalId,
            requestId);
        var resolutionKey = InputResolutionKey(answers);
        PendingInteraction? interaction;
        lock (interactionLock)
        {
            if (completed.TryGetValue(key, out var completedInteraction))
            {
                if (string.Equals(
                    completedInteraction.ResolutionKey,
                    resolutionKey,
                    StringComparison.Ordinal))
                {
                    return Task.CompletedTask;
                }
                throw new InvalidOperationException(
                    "托管终端交互已经使用不同响应完成。");
            }
            pending.TryGetValue(key, out interaction);
        }
        if (interaction is null)
        {
            throw new InvalidOperationException("找不到对应的托管终端问题。");
        }
        Complete(
            key,
            BuildInputResponse(runtime, interaction.Hook, answers),
            resolutionKey);
        return Task.CompletedTask;
    }

    public Task DeferInputToLocalAsync(
        string runtime,
        string sessionExternalId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        cancellationToken.ThrowIfCancellationRequested();
        Complete(
            new(InteractionKind.Input, runtime, sessionExternalId, requestId),
            EmptyResponse,
            "input:local");
        return Task.CompletedTask;
    }

    public void ReleaseSession(string runtime, string sessionExternalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionExternalId);
        ActivitySessionQueue? activityQueue;
        lock (activityQueueLock)
        {
            activityQueues.Remove(
                new ActivityQueueKey(runtime, sessionExternalId),
                out activityQueue);
        }
        if (activityQueue is not null)
        {
            activityQueue.Dispose();
        }
        List<PendingInteraction> released = [];
        lock (interactionLock)
        {
            var keys = pending.Keys
                .Where(key =>
                    string.Equals(key.Runtime, runtime, StringComparison.Ordinal) &&
                    string.Equals(
                        key.SessionId,
                        sessionExternalId,
                        StringComparison.Ordinal))
                .ToArray();
            foreach (var key in keys)
            {
                var interaction = pending[key];
                pending.Remove(key);
                RememberCompleted(key, EmptyResponse, "local");
                released.Add(interaction);
            }
        }
        foreach (var interaction in released)
        {
            interaction.Completion.TrySetResult(EmptyResponse.Clone());
        }
    }

    public void Dispose()
    {
        if (activityLifetime.IsCancellationRequested)
        {
            return;
        }
        activityLifetime.Cancel();
        ActivitySessionQueue[] queues;
        lock (activityQueueLock)
        {
            queues = activityQueues.Values.ToArray();
            activityQueues.Clear();
        }
        foreach (var queue in queues)
        {
            queue.Dispose();
        }
        activityLifetime.Dispose();
    }

    private async Task FlushActivitiesAsync(
        JsonElement hook,
        CancellationToken cancellationToken)
    {
        if (hook.ValueKind is not JsonValueKind.Object ||
            OptionalString(hook, "session_id") is not { } sessionId)
        {
            return;
        }
        var runtime = OptionalString(hook, "runtime") ?? RuntimeNames.Codex;
        ActivitySessionQueue? queue;
        lock (activityQueueLock)
        {
            activityQueues.TryGetValue(
                new ActivityQueueKey(runtime, sessionId),
                out queue);
        }
        if (queue is not null)
        {
            await queue.FlushAsync(cancellationToken);
        }
    }

    private async Task ProcessActivityAsync(QueuedActivity activity)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                activityLifetime.Token);
            timeout.CancelAfter(activityTimeout);
            await eventSink.PublishAsync(activity.Event, timeout.Token);
        }
        catch
        {
            // Progress events are deliberately best effort. Releasing the
            // fingerprint allows a later retry to be accepted after overload.
            normalizer.Release(activity.Hook);
        }
    }

    private void ReleaseActivity(QueuedActivity activity) =>
        normalizer.Release(activity.Hook);

    private static JsonElement BuildInputResponse(
        string runtime,
        JsonElement hook,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var input = hook.TryGetProperty("tool_input", out var toolInput) &&
            toolInput.ValueKind == JsonValueKind.Object
                ? toolInput
                : default;
        if (runtime == RuntimeNames.ClaudeCode &&
            input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty("claudeCodeOriginalInput", out var original) &&
            original.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty("claudeCodeQuestionTextById", out var textById) &&
            textById.ValueKind == JsonValueKind.Object)
        {
            var updatedInput = JsonNode.Parse(original.GetRawText())!.AsObject();
            var nativeAnswers = new JsonObject();
            var annotations = new JsonObject();
            foreach (var answer in answers)
            {
                if (!textById.TryGetProperty(answer.Key, out var questionTextElement) ||
                    questionTextElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var questionText = questionTextElement.GetString()!;
                nativeAnswers[questionText] = string.Join(", ", answer.Value);
                var preview = SingleAnswerPreview(input, answer.Key, answer.Value);
                if (preview is not null)
                {
                    annotations[questionText] = new JsonObject { ["preview"] = preview };
                }
            }
            updatedInput["answers"] = nativeAnswers;
            updatedInput["annotations"] = annotations;
            var root = new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "allow",
                    ["updatedInput"] = updatedInput,
                },
            };
            return JsonSerializer.SerializeToElement(root);
        }

        var answerText = BuildAnswerText(input, answers);
        return JsonSerializer.SerializeToElement(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = "deny",
                permissionDecisionReason =
                    $"request_user_input 已由用户通过飞书回答：\n{answerText}\n请直接使用这些答案继续，不要再次询问同一组问题。",
            },
        });
    }

    private static string? SingleAnswerPreview(
        JsonElement input,
        string questionId,
        IReadOnlyList<string> selected)
    {
        if (selected.Count != 1 ||
            !input.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var question in questions.EnumerateArray())
        {
            if (OptionalString(question, "id") != questionId ||
                !question.TryGetProperty("options", out var options) ||
                options.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var option in options.EnumerateArray())
            {
                if (OptionalString(option, "label") == selected[0])
                {
                    return OptionalString(option, "preview");
                }
            }
        }
        return null;
    }

    private static string BuildAnswerText(
        JsonElement input,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
        {
            return string.Join("\n", answers.Select(answer =>
                $"{answer.Key}: {string.Join("、", answer.Value)}"));
        }
        return string.Join("\n", questions.EnumerateArray().Select((question, index) =>
        {
            var id = OptionalString(question, "id") ?? $"question_{index + 1}";
            var header = OptionalString(question, "header") ?? id;
            return $"{index + 1}. {header} ({id}): {string.Join("、", answers.GetValueOrDefault(id) ?? [])}";
        }));
    }

    private static InteractionDescriptor? DescribeInteraction(JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var runtime = OptionalString(hook, "runtime") ?? RuntimeNames.Codex;
        var sessionId = OptionalString(hook, "session_id");
        var eventName = OptionalString(hook, "hook_event_name");
        var toolName = OptionalString(hook, "tool_name");
        var requestId = OptionalString(hook, "tool_use_id") ?? OptionalString(hook, "turn_id");
        var kind = eventName switch
        {
            "PermissionRequest" => InteractionKind.Approval,
            "PreToolUse" when toolName == "request_user_input" => InteractionKind.Input,
            _ => (InteractionKind?)null,
        };
        return kind is null || string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(requestId)
                ? null
                : new(kind.Value, new(kind.Value, runtime, sessionId, requestId));
    }

    private void Complete(
        InteractionKey key,
        JsonElement response,
        string resolutionKey)
    {
        PendingInteraction? interaction;
        lock (interactionLock)
        {
            if (completed.TryGetValue(key, out var completedResponse))
            {
                if (string.Equals(
                        completedResponse.ResolutionKey,
                        resolutionKey,
                        StringComparison.Ordinal) &&
                    ResponsesEqual(completedResponse.Response, response))
                {
                    return;
                }
                throw new InvalidOperationException(
                    "托管终端交互已经使用不同响应完成。");
            }
            if (!pending.Remove(key, out interaction))
            {
                throw new InvalidOperationException("找不到对应的托管终端交互请求。");
            }
            RememberCompleted(key, response, resolutionKey);
        }
        interaction.Completion.TrySetResult(response.Clone());
    }

    private void Fail(InteractionKey key, PendingInteraction interaction, Exception error)
    {
        lock (interactionLock)
        {
            if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, interaction))
            {
                pending.Remove(key);
            }
        }
        interaction.Completion.TrySetException(error);
    }

    private void ReleaseWaiter(InteractionKey key, PendingInteraction interaction)
    {
        lock (interactionLock)
        {
            if (interaction.WaiterCount > 0)
            {
                interaction.WaiterCount--;
            }
            if (interaction.WaiterCount == 0 &&
                pending.TryGetValue(key, out var current) &&
                ReferenceEquals(current, interaction))
            {
                pending.Remove(key);
            }
        }
    }

    private void RememberCompleted(
        InteractionKey key,
        JsonElement response,
        string resolutionKey)
    {
        completed[key] = new(response.Clone(), resolutionKey);
        completedOrder.Enqueue(key);
        while (completedOrder.Count > capacity)
        {
            completed.Remove(completedOrder.Dequeue());
        }
    }

    private static bool ResponsesEqual(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(
            JsonNode.Parse(left.GetRawText()),
            JsonNode.Parse(right.GetRawText()));

    private static string InputResolutionKey(
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var normalized = answers
            .OrderBy(answer => answer.Key, StringComparer.Ordinal)
            .ToDictionary(
                answer => answer.Key,
                answer => answer.Value.ToArray(),
                StringComparer.Ordinal);
        return $"input:{JsonSerializer.Serialize(normalized)}";
    }

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static TimeSpan PositiveTimeout(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);

    private readonly record struct ActivityQueueKey(string Runtime, string SessionId);

    private sealed record QueuedActivity(
        RuntimeEventEnvelope Event,
        JsonElement Hook)
    {
        public bool Important => Event.EventType is
            RuntimeEventTypes.TurnStarted or RuntimeEventTypes.TurnFailed;
    }

    private abstract record ActivityQueueItem;

    private sealed record ActivityWork(QueuedActivity Activity) : ActivityQueueItem;

    private sealed record ActivityBarrier(
        TaskCompletionSource Completion) : ActivityQueueItem;

    private sealed class ActivitySessionQueue : IDisposable
    {
        private readonly object sync = new();
        private readonly int capacity;
        private readonly Func<QueuedActivity, Task> processor;
        private readonly Action<QueuedActivity> release;
        private readonly LinkedList<ActivityQueueItem> pending = [];
        private int activityCount;
        private bool workerRunning;
        private bool disposed;

        public ActivitySessionQueue(
            int capacity,
            Func<QueuedActivity, Task> processor,
            Action<QueuedActivity> release)
        {
            this.capacity = capacity;
            this.processor = processor;
            this.release = release;
        }

        public bool Enqueue(QueuedActivity activity)
        {
            QueuedActivity? dropped = null;
            var startWorker = false;
            lock (sync)
            {
                if (disposed)
                {
                    return false;
                }
                if (activityCount >= capacity)
                {
                    var candidate = pending.First;
                    while (candidate is not null &&
                           candidate.Value is not ActivityWork { Activity.Important: false })
                    {
                        candidate = candidate.Next;
                    }
                    if (candidate is null && !activity.Important)
                    {
                        return false;
                    }
                    candidate ??= pending.First;
                    while (candidate is not null && candidate.Value is not ActivityWork)
                    {
                        candidate = candidate.Next;
                    }
                    if (candidate?.Value is ActivityWork oldest)
                    {
                        dropped = oldest.Activity;
                        pending.Remove(candidate);
                        activityCount--;
                    }
                }
                pending.AddLast(new ActivityWork(activity));
                activityCount++;
                if (!workerRunning)
                {
                    workerRunning = true;
                    startWorker = true;
                }
            }
            if (dropped is not null)
            {
                release(dropped);
            }
            if (startWorker)
            {
                _ = Task.Run(ProcessAsync);
            }
            return true;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource? barrier = null;
            var startWorker = false;
            lock (sync)
            {
                if (disposed || !workerRunning && pending.Count == 0)
                {
                    return Task.CompletedTask;
                }
                barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.AddLast(new ActivityBarrier(barrier));
                if (!workerRunning)
                {
                    workerRunning = true;
                    startWorker = true;
                }
            }
            if (startWorker)
            {
                _ = Task.Run(ProcessAsync);
            }
            return barrier.Task.WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            List<QueuedActivity> dropped = [];
            List<TaskCompletionSource> barriers = [];
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                foreach (var item in pending)
                {
                    switch (item)
                    {
                        case ActivityWork work:
                            dropped.Add(work.Activity);
                            break;
                        case ActivityBarrier barrier:
                            barriers.Add(barrier.Completion);
                            break;
                    }
                }
                pending.Clear();
                activityCount = 0;
            }
            foreach (var activity in dropped)
            {
                release(activity);
            }
            foreach (var barrier in barriers)
            {
                barrier.TrySetCanceled();
            }
        }

        private async Task ProcessAsync()
        {
            while (true)
            {
                ActivityQueueItem item;
                lock (sync)
                {
                    if (disposed || pending.First is null)
                    {
                        workerRunning = false;
                        return;
                    }
                    item = pending.First.Value;
                    pending.RemoveFirst();
                    if (item is ActivityWork)
                    {
                        activityCount--;
                    }
                }

                switch (item)
                {
                    case ActivityWork work:
                        try
                        {
                            await processor(work.Activity);
                        }
                        catch
                        {
                            release(work.Activity);
                        }
                        break;
                    case ActivityBarrier barrier:
                        barrier.Completion.TrySetResult();
                        break;
                }
            }
        }
    }

    private enum InteractionKind
    {
        Approval,
        Input,
    }

    private readonly record struct InteractionKey(
        InteractionKind Kind,
        string Runtime,
        string SessionId,
        string RequestId);

    private readonly record struct InteractionDescriptor(
        InteractionKind Kind,
        InteractionKey Key);

    private sealed record PendingInteraction(InteractionKind Kind, JsonElement Hook)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WaiterCount { get; set; }
    }

    private sealed record CompletedInteraction(
        JsonElement Response,
        string ResolutionKey);
}
