using AiCliFeishu.Bridge.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Core.Tests;

[TestClass]
public sealed class RuntimeAdapterTests
{
    [TestMethod]
    public void RegistryResolvesAllThreeRuntimes()
    {
        var registry = new RuntimeAdapterRegistry();
        var codex = Adapter(RuntimeNames.Codex, RuntimeCapability.PromptSend);
        var claude = Adapter(RuntimeNames.ClaudeCode, RuntimeCapability.PromptSend);
        var opencode = Adapter(RuntimeNames.OpenCode, RuntimeCapability.PromptSend);

        registry.Register(codex);
        registry.Register(claude);
        registry.Register(opencode);

        Assert.AreSame(codex, registry.ForRuntime(RuntimeNames.Codex));
        Assert.AreSame(claude, registry.ForRuntime(RuntimeNames.ClaudeCode));
        Assert.AreSame(opencode, registry.ForRuntime(RuntimeNames.OpenCode));
    }

    [TestMethod]
    public void DuplicateAndMissingAdaptersFailClearly()
    {
        var registry = new RuntimeAdapterRegistry();
        registry.Register(Adapter(RuntimeNames.Codex, RuntimeCapability.PromptSend));

        Assert.ThrowsException<InvalidOperationException>(
            () => registry.Register(
                Adapter(RuntimeNames.Codex, RuntimeCapability.PromptSend)));
        Assert.ThrowsException<KeyNotFoundException>(
            () => registry.ForRuntime(RuntimeNames.OpenCode));
    }

    [TestMethod]
    public async Task DispatcherPreservesTheStandardCommand()
    {
        var registry = new RuntimeAdapterRegistry();
        var adapter = Adapter(RuntimeNames.ClaudeCode, RuntimeCapability.PromptSend);
        registry.Register(adapter);
        var command = BridgeProtocolJson.DeserializeCommand(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "ProtocolExamples",
                    "prompt-send.json")));

        await new RuntimeCommandDispatcher(registry).DispatchAsync(command);

        Assert.AreSame(command, adapter.LastCommand);
        Assert.AreEqual(
            "继续执行并汇报结果",
            adapter.LastCommand!.Payload.GetProperty("prompt").GetString());
    }

    [TestMethod]
    public async Task QueueModeRequiresBothSendAndQueueCapabilities()
    {
        var registry = new RuntimeAdapterRegistry();
        var adapter = Adapter(RuntimeNames.Codex, RuntimeCapability.PromptSend);
        registry.Register(adapter);
        var command = BridgeProtocolJson.DeserializeCommand("""
            {
              "protocolVersion": 1,
              "commandId": "command-queue-1",
              "commandType": "prompt.send",
              "createdAt": "2026-08-05T10:00:00.000Z",
              "runtime": "codex",
              "session": { "externalId": "session-1" },
              "traceId": "trace-queue-1",
              "payload": { "prompt": "排队执行", "mode": "queue" }
            }
            """);

        var error = await Assert.ThrowsExceptionAsync<NotSupportedException>(
            () => new RuntimeCommandDispatcher(registry).DispatchAsync(command));

        StringAssert.Contains(error.Message, nameof(RuntimeCapability.PromptQueue));
        Assert.IsNull(adapter.LastCommand);
    }

    [TestMethod]
    public async Task InvalidCommandNeverReachesTheAdapter()
    {
        var registry = new RuntimeAdapterRegistry();
        var adapter = Adapter(RuntimeNames.Codex, RuntimeCapability.PromptSend);
        registry.Register(adapter);
        var command = BridgeProtocolJson.DeserializeCommand("""
            {
              "protocolVersion": 9,
              "commandId": "command-invalid-1",
              "commandType": "prompt.send",
              "createdAt": "invalid",
              "runtime": "codex",
              "session": { "externalId": "session-1" },
              "traceId": "trace-invalid-1",
              "payload": { "prompt": "继续", "mode": "steer" }
            }
            """);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => new RuntimeCommandDispatcher(registry).DispatchAsync(command));
        Assert.IsNull(adapter.LastCommand);
    }

    private static FakeRuntimeAdapter Adapter(
        string runtime,
        params RuntimeCapability[] capabilities)
    {
        return new(runtime, capabilities);
    }

    private sealed class FakeRuntimeAdapter(
        string runtime,
        IEnumerable<RuntimeCapability> capabilities) : IRuntimeAdapter
    {
        public string Runtime { get; } = runtime;

        public IReadOnlySet<RuntimeCapability> Capabilities { get; } =
            new HashSet<RuntimeCapability>(capabilities);

        public RuntimeCommandEnvelope? LastCommand { get; private set; }

        public bool IsReady(RuntimeSession session) => true;

        public Task ExecuteAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.CompletedTask;
        }
    }
}
