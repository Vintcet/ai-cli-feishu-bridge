using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

[TestClass]
public sealed class RuntimeAdapterContractTests
{
    public static IEnumerable<object[]> Runtimes =>
    [
        [RuntimeNames.Codex],
        [RuntimeNames.ClaudeCode],
        [RuntimeNames.OpenCode],
    ];

    [DataTestMethod]
    [DynamicData(nameof(Runtimes))]
    public void RuntimeIdentityCapabilitiesAndReadinessMatch(string runtime)
    {
        var harness = RuntimeAdapterHarness.Create(runtime);

        Assert.AreEqual(runtime, harness.Adapter.Runtime);
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.PromptSend));
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.ApprovalResolve));
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.InputResolve));
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.SessionLaunch));
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.SessionResume));
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.SessionStop));
        Assert.IsTrue(harness.Adapter.Capabilities.Contains(RuntimeCapability.ActivityStream));
        Assert.AreEqual(
            runtime != RuntimeNames.OpenCode,
            harness.Adapter.Capabilities.Contains(RuntimeCapability.PromptQueue));
        Assert.IsTrue(harness.Adapter.IsReady(new RuntimeSession("session-1", "C:/repo")));
    }

    [DataTestMethod]
    [DataRow(RuntimeNames.Codex)]
    [DataRow(RuntimeNames.ClaudeCode)]
    public void PendingHookMakesManagedAdapterReadyWithoutTerminal(string runtime)
    {
        var harness = RuntimeAdapterHarness.Create(runtime);
        var ports = (FakeManagedRuntimePorts)harness.Recorder;
        ports.Ready = false;
        ports.HookReady = false;
        Assert.IsFalse(harness.Adapter.IsReady(new RuntimeSession("session-1")));

        ports.HookReady = true;

        Assert.IsTrue(harness.Adapter.IsReady(new RuntimeSession("session-1")));
    }

    [DataTestMethod]
    [DynamicData(nameof(Runtimes))]
    public async Task StandardCommandsReachTheRuntimePortWithContext(string runtime)
    {
        var harness = RuntimeAdapterHarness.Create(runtime);
        var registry = new RuntimeAdapterRegistry();
        registry.Register(harness.Adapter);
        var dispatcher = new RuntimeCommandDispatcher(registry);
        foreach (var commandType in new[]
                 {
                     RuntimeCommandTypes.PromptSend,
                     RuntimeCommandTypes.ApprovalResolve,
                     RuntimeCommandTypes.InputResolve,
                     RuntimeCommandTypes.SessionLaunch,
                     RuntimeCommandTypes.SessionResume,
                     RuntimeCommandTypes.SessionStop,
                 })
        {
            await dispatcher.DispatchAsync(Command(runtime, commandType));
        }

        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeCommandTypes.PromptSend,
                RuntimeCommandTypes.ApprovalResolve,
                RuntimeCommandTypes.InputResolve,
                RuntimeCommandTypes.SessionLaunch,
                RuntimeCommandTypes.SessionResume,
                RuntimeCommandTypes.SessionStop,
            },
            harness.Recorder.Calls.Select(call => call.Operation).ToArray());
        Assert.IsTrue(harness.Recorder.Calls.All(call => call.SessionId == "session-1"));
        Assert.IsTrue(harness.Recorder.Calls.All(call => call.Context.TraceId == "trace-contract"));
        Assert.IsTrue(harness.Recorder.Calls.All(call => call.Context.CorrelationId == "correlation-contract"));
        Assert.IsTrue(harness.Recorder.Calls.All(call =>
            call.Context.CommandId.StartsWith("command-", StringComparison.Ordinal)));
    }

    [DataTestMethod]
    [DynamicData(nameof(Runtimes))]
    public async Task InvalidCommandNeverReachesTheRuntimePort(string runtime)
    {
        var harness = RuntimeAdapterHarness.Create(runtime);
        var registry = new RuntimeAdapterRegistry();
        registry.Register(harness.Adapter);
        var invalid = BridgeProtocolJson.DeserializeCommand($$"""
            {
              "protocolVersion": 9,
              "commandId": "command-invalid",
              "commandType": "prompt.send",
              "createdAt": "not-a-time",
              "runtime": "{{runtime}}",
              "session": { "externalId": "session-1" },
              "traceId": "trace-invalid",
              "payload": { "prompt": "继续", "mode": "steer" }
            }
            """);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => new RuntimeCommandDispatcher(registry).DispatchAsync(invalid));
        Assert.AreEqual(0, harness.Recorder.Calls.Count);
    }

    [DataTestMethod]
    [DynamicData(nameof(Runtimes))]
    public async Task RuntimePortFailureIsNotReportedAsSuccess(string runtime)
    {
        var harness = RuntimeAdapterHarness.Create(runtime);
        harness.Recorder.NextError = new IOException("transport failed");
        var registry = new RuntimeAdapterRegistry();
        registry.Register(harness.Adapter);

        var error = await Assert.ThrowsExceptionAsync<IOException>(
            () => new RuntimeCommandDispatcher(registry).DispatchAsync(
                Command(runtime, RuntimeCommandTypes.PromptSend)));

        Assert.AreEqual("transport failed", error.Message);
        Assert.AreEqual(1, harness.Recorder.Calls.Count);
    }

    [TestMethod]
    public async Task OpenCodeQueueCommandFailsBeforeTheTransport()
    {
        var harness = RuntimeAdapterHarness.Create(RuntimeNames.OpenCode);
        var registry = new RuntimeAdapterRegistry();
        registry.Register(harness.Adapter);
        var command = BridgeProtocolJson.DeserializeCommand("""
            {
              "protocolVersion": 1,
              "commandId": "command-queue",
              "commandType": "prompt.send",
              "createdAt": "2026-08-06T00:00:00Z",
              "runtime": "opencode",
              "session": { "externalId": "session-1" },
              "traceId": "trace-queue",
              "payload": { "prompt": "稍后执行", "mode": "queue" }
            }
            """);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => new RuntimeCommandDispatcher(registry).DispatchAsync(command));
        Assert.AreEqual(0, harness.Recorder.Calls.Count);
    }

    private static RuntimeCommandEnvelope Command(string runtime, string commandType)
    {
        var payload = commandType switch
        {
            RuntimeCommandTypes.PromptSend => """{ "prompt": "继续", "mode": "steer" }""",
            RuntimeCommandTypes.ApprovalResolve =>
                """{ "requestId": "approval-1", "decision": "allow_once" }""",
            RuntimeCommandTypes.InputResolve =>
                """{ "requestId": "input-1", "answers": { "opencode_question_1": ["答案"] } }""",
            RuntimeCommandTypes.SessionLaunch =>
                """{ "cwd": "C:/repo", "prompt": "开始", "elevated": false }""",
            RuntimeCommandTypes.SessionResume => """{ "prompt": "继续" }""",
            RuntimeCommandTypes.SessionStop => """{ "reason": "测试" }""",
            _ => throw new ArgumentOutOfRangeException(nameof(commandType)),
        };
        return BridgeProtocolJson.DeserializeCommand($$"""
            {
              "protocolVersion": 1,
              "commandId": "command-{{commandType.Replace('.', '-')}}",
              "commandType": "{{commandType}}",
              "createdAt": "2026-08-06T00:00:00Z",
              "runtime": "{{runtime}}",
              "session": { "externalId": "session-1", "cwd": "C:/repo" },
              "traceId": "trace-contract",
              "correlationId": "correlation-contract",
              "payload": {{payload}}
            }
            """);
    }

    [TestMethod]
    public async Task OpenCodeAnswersAreOrderedByNormalizedQuestionId()
    {
        var harness = RuntimeAdapterHarness.Create(RuntimeNames.OpenCode);
        var command = BridgeProtocolJson.DeserializeCommand("""
            {
              "protocolVersion": 1,
              "commandId": "command-input-order",
              "commandType": "input.resolve",
              "createdAt": "2026-08-06T00:00:00Z",
              "runtime": "opencode",
              "session": { "externalId": "session-1" },
              "traceId": "trace-input-order",
              "payload": {
                "requestId": "input-1",
                "answers": {
                  "opencode_question_2": ["第二题"],
                  "opencode_question_1": ["第一题"]
                }
              }
            }
            """);

        await harness.Adapter.ExecuteAsync(command);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            harness.Recorder.Calls.Single().Payload);
        var answers = payload.GetProperty("answers");
        Assert.AreEqual("第一题", answers[0][0].GetString());
        Assert.AreEqual("第二题", answers[1][0].GetString());
    }

    [TestMethod]
    public async Task OpenCodeRejectsNonContiguousQuestionIdsBeforeTransport()
    {
        var harness = RuntimeAdapterHarness.Create(RuntimeNames.OpenCode);
        var command = BridgeProtocolJson.DeserializeCommand("""
            {
              "protocolVersion": 1,
              "commandId": "command-input-gap",
              "commandType": "input.resolve",
              "createdAt": "2026-08-06T00:00:00Z",
              "runtime": "opencode",
              "session": { "externalId": "session-1" },
              "traceId": "trace-input-gap",
              "payload": {
                "requestId": "input-1",
                "answers": { "opencode_question_2": ["第二题"] }
              }
            }
            """);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => harness.Adapter.ExecuteAsync(command));
        Assert.AreEqual(0, harness.Recorder.Calls.Count);
    }
}
