using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class ManagedHookRelayTests
{
    [TestMethod]
    public void ManagedHookUsesTerminalSecretInsteadOfControlToken()
    {
        var terminalSecret = new string('a', 64);
        Environment.SetEnvironmentVariable(
            "AI_CLI_FEISHU_MANAGED_TERMINAL_SECRET",
            terminalSecret);
        Environment.SetEnvironmentVariable(
            "AI_CLI_FEISHU_CONTROL_TOKEN",
            new string('b', 64));
        try
        {
            var authentication = ManagedHookRelay.ResolveAuthentication(
                new JsonObject { ["managed_terminal_id"] = "terminal-12345678" },
                Path.GetTempPath());

            Assert.AreEqual(
                ManagedHookRelay.TerminalSecretHeader,
                authentication.HeaderName);
            Assert.AreEqual(terminalSecret, authentication.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "AI_CLI_FEISHU_MANAGED_TERMINAL_SECRET",
                null);
            Environment.SetEnvironmentVariable("AI_CLI_FEISHU_CONTROL_TOKEN", null);
        }
    }

    [TestMethod]
    public void GlobalHookReadsControlTokenFromBridgeStoreMirror()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hook-auth-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "data");
        var fileToken = new string('a', 64);
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable(
            "AI_CLI_FEISHU_CONTROL_TOKEN",
            new string('b', 64));
        try
        {
            File.WriteAllText(
                Path.Combine(data, "control-token.json"),
                $$"""{"token":"{{fileToken}}"}""");

            var authentication = ManagedHookRelay.ResolveAuthentication(
                new JsonObject(),
                root);

            Assert.AreEqual(
                ManagedHookRelay.ControlTokenHeader,
                authentication.HeaderName);
            Assert.AreEqual(fileToken, authentication.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_CLI_FEISHU_CONTROL_TOKEN", null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ClaudeHookEventsResolveWithoutCommandSpecificKinds()
    {
        var expected = new Dictionary<string, string>
        {
            ["SessionStart"] = "session-start",
            ["SessionEnd"] = "session-end",
            ["PermissionRequest"] = "permission",
            ["PreToolUse"] = "pre-tool-use",
            ["PostToolUse"] = "post-tool-use",
            ["PostToolUseFailure"] = "activity",
            ["PreCompact"] = "activity",
            ["PostCompact"] = "activity",
            ["UserPromptSubmit"] = "activity",
            ["Stop"] = "stop",
        };

        foreach (var item in expected)
        {
            Assert.AreEqual(item.Value, ManagedHookRelay.ClaudeHookKind(item.Key));
        }
        Assert.IsNull(ManagedHookRelay.ClaudeHookKind("Unknown"));
    }

    [TestMethod]
    public void HookLocatorAcceptsOnlyLocalHttpEndpoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hook-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "active-install.json");
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "bridgeRoot": "C:\\bridge",
                  "bridgeUrl": "http://127.0.0.1:8765"
                }
                """);

            var locator = ManagedHookRelay.ReadHookRelayLocator(path);

            Assert.AreEqual(1, locator.SchemaVersion);
            Assert.AreEqual("C:\\bridge", locator.BridgeRoot);
            Assert.AreEqual("http://127.0.0.1:8765", locator.BridgeUrl);

            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "bridgeRoot": "C:\\bridge",
                  "bridgeUrl": "https://example.com"
                }
                """);
            Assert.ThrowsException<InvalidDataException>(() =>
                ManagedHookRelay.ReadHookRelayLocator(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ClaudeQuestionIsNormalizedForTheSharedInputPipeline()
    {
        var payload = JsonNode.Parse("""
            {
              "hook_event_name": "PreToolUse",
              "session_id": "session-1",
              "tool_use_id": "tool-1",
              "tool_name": "AskUserQuestion",
              "tool_input": {
                "questions": [
                  {
                    "question": "请选择方案",
                    "header": "方案",
                    "multiSelect": false,
                    "options": [
                      { "label": "方案 A", "description": "使用 A" },
                      { "label": "方案 B", "description": "使用 B" }
                    ]
                  }
                ]
              }
            }
            """)!.AsObject();

        var normalized = ManagedHookRelay.NormalizeClaudeCodePayload(
            payload,
            DateTimeOffset.Parse("2026-08-10T00:00:00Z"));

        Assert.AreEqual("claudecode", normalized["runtime"]!.GetValue<string>());
        Assert.AreEqual("request_user_input", normalized["tool_name"]!.GetValue<string>());
        Assert.AreEqual(
            "claudecode-session-1-tool-1",
            normalized["turn_id"]!.GetValue<string>());
        var question = normalized["tool_input"]!["questions"]![0]!.AsObject();
        Assert.AreEqual("claude_question_1", question["id"]!.GetValue<string>());
        Assert.AreEqual("请选择方案", question["question"]!.GetValue<string>());
        Assert.AreEqual("方案 A", question["options"]![0]!["label"]!.GetValue<string>());
        Assert.IsTrue(question["custom"]!.GetValue<bool>());
    }

    [TestMethod]
    public void ActivityPayloadKeepsOnlyBoundedBridgeFields()
    {
        var payload = new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["session_id"] = "session-1",
            ["tool_name"] = "shell_command",
            ["tool_input"] = new JsonObject { ["command"] = new string('x', 2_000) },
            ["tool_response"] = new JsonObject { ["ok"] = true },
            ["secret"] = "must-not-survive",
        };

        var compact = ManagedHookRelay.CompactActivityPayload(payload);

        Assert.AreEqual("session-1", compact["session_id"]!.GetValue<string>());
        Assert.IsNull(compact["secret"]);
        Assert.IsTrue(compact["tool_preview"]!.GetValue<string>().Length <= 1_200);
        StringAssert.Contains(compact["tool_response_preview"]!.GetValue<string>(), "true");
    }

    [TestMethod]
    public void AssistantAncestorWalkStopsAtCodexOrClaude()
    {
        var codex = ManagedHookRelay.FindAssistantAncestor(
            400,
            [
                new(400, 300, "AiCliFeishuTerminalHost"),
                new(300, 200, "pwsh"),
                new(200, 100, "codex"),
                new(100, 0, "explorer"),
            ]);
        var unrelated = ManagedHookRelay.FindAssistantAncestor(
            500,
            [
                new(500, 100, "AiCliFeishuTerminalHost"),
                new(100, 0, "explorer"),
            ]);

        Assert.AreEqual(200, codex?.ProcessId);
        Assert.IsNull(unrelated);
    }
}
