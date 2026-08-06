using AiCliFeishu.Bridge.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Core.Tests;

[TestClass]
public sealed class ProtocolContractTests
{
    [TestMethod]
    public void SharedPromptCommandExampleIsValid()
    {
        var command = BridgeProtocolJson.DeserializeCommand(ReadExample("prompt-send.json"));
        var result = BridgeProtocolValidator.Validate(command);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.AreEqual(BridgeProtocolVersion.Current, command.ProtocolVersion);
        Assert.AreEqual(RuntimeNames.ClaudeCode, command.Runtime);
        Assert.AreEqual(RuntimeCommandTypes.PromptSend, command.CommandType);
        Assert.AreEqual(
            "继续执行并汇报结果",
            command.Payload.GetProperty("prompt").GetString());
    }

    [TestMethod]
    public void SharedApprovalEventExampleIsValid()
    {
        var runtimeEvent = BridgeProtocolJson.DeserializeEvent(
            ReadExample("approval-requested.json"));
        var result = BridgeProtocolValidator.Validate(runtimeEvent);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.AreEqual(RuntimeNames.Codex, runtimeEvent.Runtime);
        Assert.AreEqual(RuntimeEventTypes.ApprovalRequested, runtimeEvent.EventType);
        Assert.AreEqual(
            "approval-1",
            runtimeEvent.Payload.GetProperty("requestId").GetString());
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-08-05T10:20:00Z"),
            runtimeEvent.Payload.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [TestMethod]
    public void InvalidProtocolVersionAndPayloadAreRejected()
    {
        const string json = """
            {
              "protocolVersion": 2,
              "commandId": "command-1",
              "commandType": "prompt.send",
              "createdAt": "2026-08-05T10:00:00.000Z",
              "runtime": "codex",
              "session": { "externalId": "session-1" },
              "traceId": "trace-1",
              "payload": { "prompt": "继续", "mode": "later" }
            }
            """;
        var result = BridgeProtocolValidator.Validate(
            BridgeProtocolJson.DeserializeCommand(json));

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(string.Join("\n", result.Errors), "protocolVersion");
        StringAssert.Contains(string.Join("\n", result.Errors), "mode");
    }

    [TestMethod]
    public void NullSessionIsRejectedWithoutCrashingTheValidator()
    {
        const string json = """
            {
              "protocolVersion": 1,
              "commandId": "command-1",
              "commandType": "session.stop",
              "createdAt": "2026-08-05T10:00:00.000Z",
              "runtime": "codex",
              "session": null,
              "traceId": "trace-1",
              "payload": {}
            }
            """;

        var result = BridgeProtocolValidator.Validate(
            BridgeProtocolJson.DeserializeCommand(json));

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(string.Join("\n", result.Errors), "session 必须是对象");
    }

    private static string ReadExample(string name)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "ProtocolExamples", name));
    }
}
