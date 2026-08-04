using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class RuntimeAndTerminalTests
{
    [TestMethod]
    public void EmptyArguments()
    {
        AssertSequence([], RuntimeArgumentParser.Parse(RuntimeCatalog.Codex, null));
        AssertSequence([], RuntimeArgumentParser.Parse(RuntimeCatalog.Codex, "   "));
    }

    [TestMethod]
    public void ResumeArguments()
    {
        AssertSequence(
            ["resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                "resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    [TestMethod]
    public void FullCodexCommand()
    {
        AssertSequence(
            ["resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                "codex resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    [TestMethod]
    public void QuotedArguments()
    {
        AssertSequence(
            ["resume", "session with spaces", "--model", "gpt 5"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                "resume \"session with spaces\" --model \"gpt 5\""));
    }

    [TestMethod]
    public void OpenCodeArguments()
    {
        AssertSequence(
            ["-s", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.OpenCode,
                "-s 019faef0-d0bb-7703-af82-17ee9b45397b"));
        AssertSequence(
            ["--port", "5100", "-s", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.OpenCode,
                "opencode --port 5100 -s 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    [TestMethod]
    public void ClaudeCodeArguments()
    {
        AssertSequence(
            ["--resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.ClaudeCode,
                "--resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
        AssertSequence(
            ["--resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.ClaudeCode,
                "claude --resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    [TestMethod]
    public void ResumeSessionIdDetection()
    {
        const string sessionId = "019faef0-d0bb-7703-af82-17ee9b45397b";
        Assert.AreEqual(
            sessionId,
            RuntimeArgumentParser.ExtractResumeSessionId(
                RuntimeCatalog.Codex,
                $"--model gpt-5 resume {sessionId}"));
        Assert.AreEqual(
            sessionId,
            RuntimeArgumentParser.ExtractResumeSessionId(
                RuntimeCatalog.ClaudeCode,
                $"-r {sessionId}"));
        Assert.AreEqual(
            sessionId,
            RuntimeArgumentParser.ExtractResumeSessionId(
                RuntimeCatalog.OpenCode,
                $"--session={sessionId}"));
        Assert.IsNull(
            RuntimeArgumentParser.ExtractResumeSessionId(
                RuntimeCatalog.Codex,
                "--model gpt-5"));
    }

    [TestMethod]
    public void ForwardedTerminalHostArguments()
    {
        AssertSequence(
            ["--port", "5103", "-s", "session with spaces", "$(Get-Date)"],
            RuntimeArgumentParser.ReadRepeatedArguments(
            [
                "--managed-terminal",
                "--runtime",
                "opencode",
                "--tool-arg",
                "--port",
                "--tool-arg",
                "5103",
                "--tool-arg",
                "-s",
                "--tool-arg",
                "session with spaces",
                "--tool-arg",
                "$(Get-Date)",
            ],
            "--tool-arg"));
    }

    [TestMethod]
    public void RuntimeCatalogValues()
    {
        Assert.AreSame(RuntimeCatalog.Codex, RuntimeCatalog.FromId(null));
        Assert.AreSame(RuntimeCatalog.ClaudeCode, RuntimeCatalog.FromId("claudecode"));
        Assert.AreSame(RuntimeCatalog.OpenCode, RuntimeCatalog.FromId("opencode"));
        Assert.IsTrue(RuntimeCatalog.Codex.UsesManagedTerminal);
        Assert.IsTrue(RuntimeCatalog.ClaudeCode.UsesManagedTerminal);
        Assert.IsFalse(RuntimeCatalog.OpenCode.UsesManagedTerminal);
        Assert.AreEqual(
            "--resume session-1",
            RuntimeCatalog.ClaudeCode.BuildResumeArguments("session-1"));
        Assert.AreEqual(
            "-s session-1",
            RuntimeCatalog.OpenCode.BuildResumeArguments("session-1"));
    }

    [TestMethod]
    public void ShellLookingTextStaysData()
    {
        var sentinel = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-parser-{Guid.NewGuid():N}.txt");
        AssertSequence(
            ["resume", "abc12345", "$(Get-Date)", ";", "New-Item", sentinel],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                $"resume abc12345 $(Get-Date) ; New-Item \"{sentinel}\""));
        Assert.IsFalse(File.Exists(sentinel), "Parser unexpectedly executed shell text.");
    }

    [TestMethod]
    public void TerminalInputDefaultsToSteer()
    {
        var request = TerminalInputParser.Parse(
            "{\"type\":\"prompt\",\"prompt\":\"hello\"}");
        Assert.AreEqual("hello", request.Prompt);
        Assert.AreEqual(TerminalSubmitMode.Steer, request.SubmitMode);
    }

    [TestMethod]
    public void TerminalInputSupportsQueue()
    {
        var request = TerminalInputParser.Parse(
            "{\"type\":\"prompt\",\"prompt\":\"next turn\",\"submitMode\":\"queue\"}");
        Assert.AreEqual("next turn", request.Prompt);
        Assert.AreEqual(TerminalSubmitMode.Queue, request.SubmitMode);
    }

    [TestMethod]
    public void TerminalInputRejectsInvalidMode()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            TerminalInputParser.Parse(
                "{\"type\":\"prompt\",\"prompt\":\"hello\",\"submitMode\":\"shell\"}"));
    }

    [TestMethod]
    public void ManagedTerminalReadsLowercaseControlTokenField()
    {
        var bridgeRoot = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-control-token-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(bridgeRoot, "data");
        var token = new string('a', 64);
        Directory.CreateDirectory(dataDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(dataDirectory, "control-token.json"),
                JsonSerializer.Serialize(new { token }));
            var readControlToken = typeof(ManagedTerminalHost).GetMethod(
                "ReadControlToken",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(readControlToken);
            Assert.AreEqual(token, readControlToken.Invoke(null, [bridgeRoot]));
        }
        finally
        {
            Directory.Delete(bridgeRoot, recursive: true);
        }
    }

    [TestMethod]
    public void HealthAcceptsFractionalFeishuTimestamps()
    {
        const string json = """
            {
              "ok": true,
              "processId": 4321,
              "activeSessions": 2,
              "feishu": {
                "state": "connected",
                "lastConnectTime": 1785510447081,
                "nextConnectTime": 1785510446683.327,
                "reconnectAttempts": 0
              },
              "sessions": [],
              "historySessions": [],
              "approvals": [],
              "settings": {}
            }
            """;
        var status = JsonSerializer.Deserialize<BridgeStatus>(json);
        Assert.IsNotNull(status);
        Assert.AreEqual(4321, status.ProcessId);
        Assert.AreEqual(2, status.ActiveSessions);
        Assert.AreEqual(1785510446683.327, status.Feishu.NextConnectTime);
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }
}
