using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveManagedHookResponseSinkTests
{
    private static readonly RuntimeCommandContext Context =
        new("command-response", "trace-response", "request-1");

    [TestMethod]
    public async Task ApprovalAndInputResponsesCompleteIngressWaitersAndRemainIdempotent()
    {
        var events = new RecordingRuntimeEventSink();
        var bridge = Bridge(events);
        var owner = new ActiveManagedHookResponseSink(
            ActiveOptions(),
            new RecordingTerminalDirectory(),
            bridge);
        using var approvalHook = JsonDocument.Parse("""
            {
              "hook_event_name": "PermissionRequest",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "approval-1",
              "tool_name": "shell_command",
              "tool_input": { "command": "git status" }
            }
            """);

        var approval = bridge.HandleAsync(approvalHook.RootElement, "trace-approval");
        await events.WaitForCountAsync(1);
        Assert.IsTrue(owner.IsReady(RuntimeNames.Codex, "session-1"));
        await owner.ResolveApprovalAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "approval-1",
            "allow_once");
        var approvalResponse = await approval;
        Assert.IsFalse(owner.IsReady(RuntimeNames.Codex, "session-1"));
        Assert.AreEqual(
            "allow",
            approvalResponse.GetProperty("hookSpecificOutput")
                .GetProperty("decision")
                .GetProperty("behavior")
                .GetString());

        await owner.ResolveApprovalAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "approval-1",
            "allow_once");

        using var inputHook = JsonDocument.Parse("""
            {
              "hook_event_name": "PreToolUse",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "input-1",
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
        var input = bridge.HandleAsync(inputHook.RootElement, "trace-input");
        await events.WaitForCountAsync(2);
        await owner.ResolveInputAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "input-1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["q1"] = ["本机"],
            });
        var inputResponse = await input;
        Assert.AreEqual(
            "deny",
            inputResponse.GetProperty("hookSpecificOutput")
                .GetProperty("permissionDecision")
                .GetString());
        await owner.ResolveInputAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "input-1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["q1"] = ["本机"],
            });
        var inputConflict = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            owner.ResolveInputAsync(
                Context,
                RuntimeNames.Codex,
                "session-1",
                "input-1",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["q1"] = ["远程"],
                }));
        StringAssert.Contains(inputConflict.Message, "不同响应");
        Assert.AreEqual(2, events.Events.Count);
    }

    [TestMethod]
    public async Task ClaimedSessionRuntimeMustMatchBeforeResponseIsReleased()
    {
        var events = new RecordingRuntimeEventSink();
        var bridge = Bridge(events);
        var directory = new RecordingTerminalDirectory
        {
            Identity = Identity(RuntimeNames.ClaudeCode),
        };
        var owner = new ActiveManagedHookResponseSink(
            ActiveOptions(),
            directory,
            bridge);
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PermissionRequest",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "approval-1",
              "tool_name": "shell_command",
              "tool_input": { "command": "git status" }
            }
            """);

        var handling = bridge.HandleAsync(hook.RootElement, "trace-hook");
        await events.WaitForCountAsync(1);
        Assert.IsFalse(owner.IsReady(RuntimeNames.Codex, "session-1"));
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            owner.ResolveApprovalAsync(
                Context,
                RuntimeNames.Codex,
                "session-1",
                "approval-1",
                "deny"));
        StringAssert.Contains(error.Message, "运行时身份不一致");
        Assert.IsFalse(handling.IsCompleted);

        directory.Identity = Identity(RuntimeNames.Codex);
        Assert.IsTrue(owner.IsReady(RuntimeNames.Codex, "session-1"));
        await owner.ResolveApprovalAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "approval-1",
            "deny");
        Assert.AreEqual(
            "deny",
            (await handling).GetProperty("hookSpecificOutput")
                .GetProperty("decision")
                .GetProperty("behavior")
                .GetString());
    }

    [TestMethod]
    public async Task PassiveOrCancelledResponseCannotReleasePendingWaiter()
    {
        var events = new RecordingRuntimeEventSink();
        var bridge = Bridge(events);
        var directory = new RecordingTerminalDirectory();
        var passive = new ActiveManagedHookResponseSink(
            BridgeHostOptions.Passive(Path.GetTempPath(), port: 0),
            directory,
            bridge);
        var active = new ActiveManagedHookResponseSink(
            ActiveOptions(),
            directory,
            bridge);
        using var hook = JsonDocument.Parse("""
            {
              "hook_event_name": "PermissionRequest",
              "runtime": "codex",
              "session_id": "session-1",
              "tool_use_id": "approval-1",
              "tool_name": "shell_command",
              "tool_input": { "command": "git status" }
            }
            """);

        var handling = bridge.HandleAsync(hook.RootElement, "trace-hook");
        await events.WaitForCountAsync(1);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.ResolveApprovalAsync(
                Context,
                RuntimeNames.Codex,
                "session-1",
                "approval-1",
                "allow_session"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            active.ResolveApprovalAsync(
                Context,
                RuntimeNames.Codex,
                "session-1",
                "approval-1",
                "allow_session",
                cancellation.Token));
        Assert.IsFalse(handling.IsCompleted);

        await active.ResolveApprovalAsync(
            Context,
            RuntimeNames.Codex,
            "session-1",
            "approval-1",
            "allow_session");
        Assert.AreEqual(
            "allow",
            (await handling).GetProperty("hookSpecificOutput")
                .GetProperty("decision")
                .GetProperty("behavior")
                .GetString());
    }

    private static BridgeManagedTerminalIdentity Identity(string runtime) =>
        new("terminal-1", "session-1", Path.GetTempPath(), runtime, false);

    private static ManagedRuntimeHookBridge Bridge(RecordingRuntimeEventSink events) =>
        new(new ManagedRuntimeHookNormalizer(), events, completedInteractionCapacity: 8);

    private static BridgeHostOptions ActiveOptions() => new(
        Path.Combine(Path.GetTempPath(), $"managed-hook-responses-{Guid.NewGuid():N}"),
        IPAddress.Loopback,
        8765,
        BridgeOwnershipMode.Active,
        "managed-hook-responses-test");

    private sealed class RecordingRuntimeEventSink : IRuntimeEventSink
    {
        public List<RuntimeEventEnvelope> Events { get; } = [];

        public Task PublishAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Events)
            {
                Events.Add(runtimeEvent);
            }
            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int count)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                lock (Events)
                {
                    if (Events.Count >= count)
                    {
                        return;
                    }
                }
                if (DateTime.UtcNow >= timeoutAt)
                {
                    throw new AssertFailedException("等待标准事件超时。");
                }
                await Task.Delay(10);
            }
        }
    }

    private sealed class RecordingTerminalDirectory
        : IBridgeManagedTerminalRegistrationDirectory
    {
        public BridgeManagedTerminalIdentity? Identity { get; set; }

        public BridgeManagedTerminalDirectorySnapshot Snapshot { get; } =
            new(true, 0, 0, 0, 0);

        public void Register(BridgeManagedTerminalRegistration registration) =>
            throw new NotSupportedException();

        public bool Unregister(string terminalId) =>
            throw new NotSupportedException();

        public BridgeManagedTerminalClaim? Claim(
            string cwd,
            string runtime,
            string sessionExternalId) => throw new NotSupportedException();

        public BridgeManagedTerminalClaim? ClaimById(
            string terminalId,
            string cwd,
            string runtime,
            string sessionExternalId,
            bool? elevated = null) => throw new NotSupportedException();

        public BridgeManagedTerminalIdentity? FindClaimBySession(
            string sessionExternalId) => Identity is not null &&
            string.Equals(
                Identity.SessionExternalId,
                sessionExternalId,
                StringComparison.Ordinal)
                    ? Identity
                    : null;

        public BridgeManagedTerminalIdentity? FindClaimByTerminal(string terminalId) =>
            throw new NotSupportedException();

        public void Release(string sessionExternalId) =>
            throw new NotSupportedException();

        public bool IsCurrent(ManagedTerminalTarget target) =>
            throw new NotSupportedException();
    }
}
