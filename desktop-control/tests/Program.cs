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
        AssertSequence([], CodexArgumentParser.Parse(null));
        AssertSequence([], CodexArgumentParser.Parse("   "));
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
            CodexArgumentParser.Parse("resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    private static void TestFullCodexCommand()
    {
        AssertSequence(
            ["resume", "019faef0-d0bb-7703-af82-17ee9b45397b"],
            CodexArgumentParser.Parse(
                "codex resume 019faef0-d0bb-7703-af82-17ee9b45397b"));
    }

    private static void TestQuotedArguments()
    {
        AssertSequence(
            ["resume", "session with spaces", "--model", "gpt 5"],
            CodexArgumentParser.Parse(
                "resume \"session with spaces\" --model \"gpt 5\""));
    }

    private static void TestShellLookingText()
    {
        var sentinel = Path.Combine(
            Path.GetTempPath(),
            $"codex-feishu-parser-{Guid.NewGuid():N}.txt");
        AssertSequence(
            ["resume", "abc12345", "$(Get-Date)", ";", "New-Item", sentinel],
            CodexArgumentParser.Parse(
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
