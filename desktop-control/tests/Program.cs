using System.Text.Json;

namespace CodexFeishuControl;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("empty arguments", TestEmptyArguments),
            ("resume arguments", TestResumeArguments),
            ("full codex command", TestFullCodexCommand),
            ("quoted arguments", TestQuotedArguments),
            ("opencode arguments", TestOpenCodeArguments),
            ("claude code arguments", TestClaudeCodeArguments),
            ("forwarded terminal host arguments", TestForwardedTerminalHostArguments),
            ("runtime catalog", TestRuntimeCatalog),
            ("shell-looking text stays data", TestShellLookingText),
            ("terminal input defaults to steer", TestTerminalInputDefaultsToSteer),
            ("terminal input supports queue", TestTerminalInputSupportsQueue),
            ("terminal input rejects invalid mode", TestTerminalInputRejectsInvalidMode),
            ("health accepts fractional Feishu timestamps", TestFractionalFeishuTimestamps),
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failures += 1;
                Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void TestEmptyArguments()
    {
        AssertSequence([], RuntimeArgumentParser.Parse(RuntimeCatalog.Codex, null));
        AssertSequence([], RuntimeArgumentParser.Parse(RuntimeCatalog.Codex, "   "));
    }

    private static void TestFractionalFeishuTimestamps()
    {
        const string json = """
            {
              "ok": true,
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
        if (status is null || status.ActiveSessions != 2)
        {
            throw new InvalidOperationException("health response was not parsed");
        }
        if (status.Feishu.NextConnectTime != 1785510446683.327)
        {
            throw new InvalidOperationException("fractional reconnect timestamp was lost");
        }
    }

    private static void TestResumeArguments()
    {
        AssertSequence(
            ["resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                "resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    private static void TestFullCodexCommand()
    {
        AssertSequence(
            ["resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                "codex resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    private static void TestQuotedArguments()
    {
        AssertSequence(
            ["resume", "session with spaces", "--model", "gpt 5"],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                "resume \"session with spaces\" --model \"gpt 5\""));
    }

    private static void TestOpenCodeArguments()
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

    private static void TestClaudeCodeArguments()
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

    private static void TestForwardedTerminalHostArguments()
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

    private static void TestRuntimeCatalog()
    {
        if (RuntimeCatalog.FromId(null) != RuntimeCatalog.Codex ||
            RuntimeCatalog.FromId("claudecode") != RuntimeCatalog.ClaudeCode ||
            RuntimeCatalog.FromId("opencode") != RuntimeCatalog.OpenCode)
        {
            throw new InvalidOperationException("runtime ids were not resolved consistently");
        }
        if (!RuntimeCatalog.Codex.UsesManagedTerminal ||
            !RuntimeCatalog.ClaudeCode.UsesManagedTerminal ||
            RuntimeCatalog.OpenCode.UsesManagedTerminal)
        {
            throw new InvalidOperationException("runtime transports are incorrect");
        }
        if (RuntimeCatalog.ClaudeCode.BuildResumeArguments("session-1") != "--resume session-1" ||
            RuntimeCatalog.OpenCode.BuildResumeArguments("session-1") != "-s session-1")
        {
            throw new InvalidOperationException("runtime resume arguments are incorrect");
        }
    }

    private static void TestShellLookingText()
    {
        var sentinel = Path.Combine(
            Path.GetTempPath(),
            $"codex-feishu-parser-{Guid.NewGuid():N}.txt");
        AssertSequence(
            ["resume", "abc12345", "$(Get-Date)", ";", "New-Item", sentinel],
            RuntimeArgumentParser.Parse(
                RuntimeCatalog.Codex,
                $"resume abc12345 $(Get-Date) ; New-Item \"{sentinel}\""));
        if (File.Exists(sentinel))
        {
            throw new InvalidOperationException("Parser unexpectedly executed shell text.");
        }
    }

    private static void TestTerminalInputDefaultsToSteer()
    {
        var request = TerminalInputParser.Parse(
            "{\"type\":\"prompt\",\"prompt\":\"hello\"}");
        if (request.Prompt != "hello" || request.SubmitMode != TerminalSubmitMode.Steer)
        {
            throw new InvalidOperationException("Default submit mode was not steer.");
        }
    }

    private static void TestTerminalInputSupportsQueue()
    {
        var request = TerminalInputParser.Parse(
            "{\"type\":\"prompt\",\"prompt\":\"next turn\",\"submitMode\":\"queue\"}");
        if (request.Prompt != "next turn" || request.SubmitMode != TerminalSubmitMode.Queue)
        {
            throw new InvalidOperationException("Queue submit mode was not parsed.");
        }
    }

    private static void TestTerminalInputRejectsInvalidMode()
    {
        try
        {
            TerminalInputParser.Parse(
                "{\"type\":\"prompt\",\"prompt\":\"hello\",\"submitMode\":\"shell\"}");
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid submit mode was accepted.");
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }
}
