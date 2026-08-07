using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class ManagedRuntimeHookNormalizerTests
{
    private static readonly DateTimeOffset FixedTime =
        DateTimeOffset.Parse("2026-08-06T01:02:03Z");

    [DataTestMethod]
    [DataRow("codex")]
    [DataRow("claudecode")]
    public void PermissionHookBecomesStandardApprovalEvent(string runtime)
    {
        var normalizer = Normalizer();
        using var hook = JsonDocument.Parse($$"""
            {
              "hook_event_name": "PermissionRequest",
              "runtime": "{{runtime}}",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "cwd": "C:/repo",
              "tool_use_id": "tool-1",
              "tool_name": "shell_command",
              "tool_input": { "command": "git status" }
            }
            """);

        var runtimeEvent = normalizer.Normalize(hook.RootElement, "trace-hook");

        Assert.IsNotNull(runtimeEvent);
        Assert.AreEqual(RuntimeEventTypes.ApprovalRequested, runtimeEvent.EventType);
        Assert.AreEqual(runtime, runtimeEvent.Runtime);
        Assert.AreEqual("session-1", runtimeEvent.Session!.ExternalId);
        Assert.AreEqual("C:/repo", runtimeEvent.Session.Cwd);
        Assert.AreEqual("trace-hook", runtimeEvent.TraceId);
        Assert.AreEqual("tool-1", runtimeEvent.CorrelationId);
        Assert.AreEqual("tool-1", runtimeEvent.Payload.GetProperty("requestId").GetString());
        Assert.AreEqual("shell_command", runtimeEvent.Payload.GetProperty("title").GetString());
        Assert.AreEqual(
            FixedTime.AddMinutes(20),
            runtimeEvent.Payload.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.IsTrue(BridgeProtocolValidator.Validate(runtimeEvent).IsValid);
    }

    [TestMethod]
    public void ClaudeQuestionHookBecomesStandardInputEventWithoutPrivatePayload()
    {
        var normalizer = Normalizer();
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PreToolUse",
              "runtime": "claudecode",
              "session_id": "session-1",
              "turn_id": "turn-1",
              "tool_use_id": "question-1",
              "cwd": "C:/repo",
              "tool_name": "request_user_input",
              "tool_input": {
                "questions": [{
                  "header": "环境",
                  "id": "q1",
                  "question": "使用哪个环境？",
                  "options": [{ "label": "本机", "description": "在本机运行" }],
                  "multiple": false,
                  "claudePrivate": "must-not-leak"
                }],
                "claudeCodeOriginalInput": { "secret": "must-not-leak" }
              }
            }
            """);

        var runtimeEvent = normalizer.Normalize(hook.RootElement, "trace-input");

        Assert.IsNotNull(runtimeEvent);
        Assert.AreEqual(RuntimeEventTypes.InputRequested, runtimeEvent.EventType);
        var json = runtimeEvent.Payload.GetRawText();
        Assert.IsFalse(json.Contains("claudePrivate", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("claudeCodeOriginalInput", StringComparison.Ordinal));
        Assert.AreEqual("使用哪个环境？", runtimeEvent.Payload
            .GetProperty("questions")[0]
            .GetProperty("prompt")
            .GetString());
        Assert.AreEqual("环境", runtimeEvent.Payload
            .GetProperty("questions")[0]
            .GetProperty("header")
            .GetString());
        Assert.IsTrue(runtimeEvent.Payload
            .GetProperty("questions")[0]
            .GetProperty("allowsCustom")
            .GetBoolean());
        Assert.IsFalse(runtimeEvent.Payload
            .GetProperty("questions")[0]
            .GetProperty("isSecret")
            .GetBoolean());
        Assert.AreEqual(
            FixedTime.AddMinutes(20),
            runtimeEvent.Payload.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.IsTrue(BridgeProtocolValidator.Validate(runtimeEvent).IsValid);
    }

    [TestMethod]
    public void InputAutoResolutionCapsTheStandardExpiry()
    {
        var normalizer = Normalizer();
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PreToolUse",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "question-1",
              "tool_name": "request_user_input",
              "tool_input": {
                "autoResolutionMs": 60000,
                "questions": [{
                  "id": "q1",
                  "question": "继续吗？",
                  "options": [{ "label": "继续" }, { "label": "停止" }],
                  "custom": false
                }]
              }
            }
            """);

        var runtimeEvent = normalizer.Normalize(hook.RootElement, "trace-input-expiry");

        Assert.IsNotNull(runtimeEvent);
        Assert.AreEqual(
            FixedTime.AddMinutes(1),
            runtimeEvent.Payload.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.IsFalse(runtimeEvent.Payload
            .GetProperty("questions")[0]
            .GetProperty("allowsCustom")
            .GetBoolean());
    }

    [TestMethod]
    public void InvalidAndUnknownHooksDoNotProduceEvents()
    {
        var normalizer = Normalizer();
        using var missingSession = JsonDocument.Parse("""
            { "hook_event_name": "SessionStart", "runtime": "codex" }
            """);
        using var unknown = JsonDocument.Parse("""
            {
              "hook_event_name": "PrivateHook",
              "runtime": "codex",
              "session_id": "session-1"
            }
            """);

        Assert.IsNull(normalizer.Normalize(missingSession.RootElement, "trace-1"));
        Assert.IsNull(normalizer.Normalize(unknown.RootElement, "trace-2"));
    }

    [TestMethod]
    public void SessionStartCarriesCanonicalManagedBindingMetadata()
    {
        var normalizer = Normalizer();
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "SessionStart",
              "runtime": "codex",
              "session_id": "session-managed",
              "cwd": "C:/repo",
              "model": "gpt-5",
              "source": "startup",
              "managed_terminal_id": "terminal-managed",
              "managed_terminal_elevated": true
            }
            """);

        var runtimeEvent = normalizer.Normalize(hook.RootElement, "trace-managed");

        Assert.IsNotNull(runtimeEvent);
        Assert.AreEqual(
            "terminal-managed",
            runtimeEvent.Payload.GetProperty("managedTerminalId").GetString());
        Assert.IsTrue(runtimeEvent.Payload.GetProperty("managedTerminalElevated").GetBoolean());
        Assert.IsTrue(runtimeEvent.Payload.GetProperty("managedByAssistant").GetBoolean());
        Assert.IsTrue(runtimeEvent.Payload.GetProperty("historyEligible").GetBoolean());
        Assert.AreEqual("startup", runtimeEvent.Payload.GetProperty("source").GetString());
    }

    [TestMethod]
    public void DuplicateHookIsIgnoredUntilItsFingerprintIsEvicted()
    {
        var normalizer = new ManagedRuntimeHookNormalizer(
            () => Guid.NewGuid().ToString("N"),
            () => FixedTime,
            deduplicationCapacity: 2);
        using var first = JsonDocument.Parse("""
            { "hook_event_name": "SessionStart", "runtime": "codex", "session_id": "session-1" }
            """);
        using var second = JsonDocument.Parse("""
            { "hook_event_name": "SessionStart", "runtime": "codex", "session_id": "session-2" }
            """);
        using var third = JsonDocument.Parse("""
            { "hook_event_name": "SessionStart", "runtime": "codex", "session_id": "session-3" }
            """);

        Assert.IsNotNull(normalizer.Normalize(first.RootElement, "trace-dedup"));
        Assert.IsNull(normalizer.Normalize(first.RootElement, "trace-dedup"));
        Assert.IsNotNull(normalizer.Normalize(second.RootElement, "trace-dedup"));
        Assert.IsNotNull(normalizer.Normalize(third.RootElement, "trace-dedup"));
        Assert.IsNotNull(normalizer.Normalize(first.RootElement, "trace-dedup"));
    }

    private static ManagedRuntimeHookNormalizer Normalizer() =>
        new(() => "event-fixed", () => FixedTime);
}
