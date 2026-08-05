using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class ManagedRuntimeHookBridgeTests
{
    private static readonly RuntimeCommandContext Context =
        new("command-hook", "trace-command", "tool-1");

    [TestMethod]
    public async Task ApprovalHookPublishesOnceWaitsAndCachesResolvedResponse()
    {
        var sink = new RecordingRuntimeEventSink();
        var bridge = Bridge(sink);
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PermissionRequest",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "tool-1",
              "tool_name": "shell_command",
              "tool_input": { "command": "git status" }
            }
            """);

        var first = bridge.HandleAsync(hook.RootElement, "trace-hook");
        await sink.FirstPublished;
        var duplicate = bridge.HandleAsync(hook.RootElement, "trace-duplicate");
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(duplicate.IsCompleted);
        Assert.AreEqual(1, sink.Events.Count);
        Assert.AreEqual(RuntimeEventTypes.ApprovalRequested, sink.Events[0].EventType);

        await bridge.ResolveApprovalAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "tool-1",
            "allow_once");
        var responses = await Task.WhenAll(first, duplicate);
        Assert.IsTrue(responses.All(response => response
            .GetProperty("hookSpecificOutput")
            .GetProperty("decision")
            .GetProperty("behavior")
            .GetString() == "allow"));

        var replay = await bridge.HandleAsync(hook.RootElement, "trace-replay");
        Assert.AreEqual("allow", replay.GetProperty("hookSpecificOutput")
            .GetProperty("decision")
            .GetProperty("behavior").GetString());
        Assert.AreEqual(1, sink.Events.Count);
    }

    [TestMethod]
    public async Task ClaudeInputResponseRestoresNativeInputShapeAndAnnotations()
    {
        var sink = new RecordingRuntimeEventSink();
        var bridge = Bridge(sink);
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PreToolUse",
              "runtime": "claudecode",
              "session_id": "session-1",
              "tool_use_id": "tool-1",
              "tool_name": "request_user_input",
              "tool_input": {
                "questions": [{
                  "header": "环境",
                  "id": "q1",
                  "question": "使用哪个环境？",
                  "options": [{ "label": "本机", "preview": "local-preview" }],
                  "multiple": false
                }],
                "claudeCodeOriginalInput": { "questions": [{ "question": "使用哪个环境？" }] },
                "claudeCodeQuestionTextById": { "q1": "使用哪个环境？" }
              }
            }
            """);

        var handling = bridge.HandleAsync(hook.RootElement, "trace-input");
        await sink.FirstPublished;
        await bridge.ResolveInputAsync(
            Context,
            RuntimeNames.ClaudeCode,
            "session-1",
            "tool-1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["q1"] = ["本机"],
            });
        var response = await handling;

        var output = response.GetProperty("hookSpecificOutput");
        Assert.AreEqual("allow", output.GetProperty("permissionDecision").GetString());
        var updated = output.GetProperty("updatedInput");
        Assert.AreEqual("本机", updated.GetProperty("answers")
            .GetProperty("使用哪个环境？").GetString());
        Assert.AreEqual("local-preview", updated.GetProperty("annotations")
            .GetProperty("使用哪个环境？")
            .GetProperty("preview").GetString());
    }

    [TestMethod]
    public async Task CodexInputResponseUsesInstructionFallback()
    {
        var sink = new RecordingRuntimeEventSink();
        var bridge = Bridge(sink);
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PreToolUse",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "tool-1",
              "tool_name": "request_user_input",
              "tool_input": {
                "questions": [{
                  "header": "环境",
                  "id": "q1",
                  "question": "使用哪个环境？",
                  "options": [{ "label": "本机" }],
                  "multiple": false
                }]
              }
            }
            """);

        var handling = bridge.HandleAsync(hook.RootElement, "trace-input");
        await sink.FirstPublished;
        await bridge.ResolveInputAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "tool-1",
            new Dictionary<string, IReadOnlyList<string>> { ["q1"] = ["本机"] });
        var response = await handling;

        var output = response.GetProperty("hookSpecificOutput");
        Assert.AreEqual("deny", output.GetProperty("permissionDecision").GetString());
        StringAssert.Contains(
            output.GetProperty("permissionDecisionReason").GetString()!,
            "1. 环境 (q1): 本机");
    }

    [TestMethod]
    public async Task AbandonedInteractiveHookCanBePublishedAgainOnRetry()
    {
        var sink = new RecordingRuntimeEventSink();
        var bridge = Bridge(sink);
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PermissionRequest",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "tool-1",
              "tool_name": "shell_command",
              "tool_input": { "command": "git status" }
            }
            """);
        using var cancellation = new CancellationTokenSource();

        var abandoned = bridge.HandleAsync(
            hook.RootElement,
            "trace-abandoned",
            cancellation.Token);
        await sink.FirstPublished;
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => abandoned);

        var retried = bridge.HandleAsync(hook.RootElement, "trace-retry");
        await WaitUntilAsync(() => sink.Events.Count == 2);
        await bridge.ResolveApprovalAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "tool-1",
            "deny");
        var response = await retried;

        Assert.AreEqual("deny", response.GetProperty("hookSpecificOutput")
            .GetProperty("decision")
            .GetProperty("behavior").GetString());
        Assert.AreEqual("trace-retry", sink.Events[1].TraceId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
            {
                throw new AssertFailedException("等待测试条件超时。");
            }
            await Task.Delay(10);
        }
    }

    private static ManagedRuntimeHookBridge Bridge(RecordingRuntimeEventSink sink) =>
        new(
            new ManagedRuntimeHookNormalizer(),
            sink,
            completedInteractionCapacity: 8);
}
