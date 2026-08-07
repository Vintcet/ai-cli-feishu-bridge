using System.Text.Json;
using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public sealed class ManagedRuntimeHookBridge(
    ManagedRuntimeHookNormalizer normalizer,
    IRuntimeEventSink eventSink,
    int completedInteractionCapacity = 1_024) : IManagedHookResponseSink
{
    private static readonly JsonElement EmptyResponse =
        JsonSerializer.SerializeToElement(new { });
    private readonly object interactionLock = new();
    private readonly Dictionary<InteractionKey, PendingInteraction> pending = [];
    private readonly Dictionary<InteractionKey, CompletedInteraction> completed = [];
    private readonly Queue<InteractionKey> completedOrder = new();
    private readonly int capacity = Math.Max(1, completedInteractionCapacity);

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

    public void ReleaseSession(string runtime, string sessionExternalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionExternalId);
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
