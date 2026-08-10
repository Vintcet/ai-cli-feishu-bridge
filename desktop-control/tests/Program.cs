using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class RuntimeAndTerminalTests
{
    private static readonly string TerminalSecret = new('a', 64);

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
    public void BridgeEnvironmentReaderParsesConfiguredCodexCommand()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllLines(
                Path.Combine(root, ".env"),
                [
                    "IGNORED=value",
                    "CODEX_COMMAND=\"C:\\Tools\\Codex CLI\\codex.exe\" # configured",
                ]);

            Assert.AreEqual(
                @"C:\Tools\Codex CLI\codex.exe",
                BridgeEnvironmentReader.Read(root, "CODEX_COMMAND"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void BridgeEnvironmentReaderPrefersProcessEnvironment()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-env-{Guid.NewGuid():N}");
        var name = $"AI_CLI_FEISHU_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"), $"{name}=file-value");
            Environment.SetEnvironmentVariable(name, "environment-value");

            Assert.AreEqual(
                "environment-value",
                BridgeEnvironmentReader.Read(root, name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Directory.Delete(root, recursive: true);
        }
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
            $$"""{"type":"prompt","commandId":"command-1","terminalSecret":"{{TerminalSecret}}","prompt":"hello"}""");
        Assert.AreEqual("command-1", request.CommandId);
        Assert.AreEqual(TerminalSecret, request.TerminalSecret);
        Assert.AreEqual("hello", request.Prompt);
        Assert.AreEqual(TerminalSubmitMode.Steer, request.SubmitMode);
    }

    [TestMethod]
    public void TerminalInputSupportsQueue()
    {
        var request = TerminalInputParser.Parse(
            $$"""{"type":"prompt","commandId":"command-2","terminalSecret":"{{TerminalSecret}}","prompt":"next turn","submitMode":"queue"}""");
        Assert.AreEqual("next turn", request.Prompt);
        Assert.AreEqual(TerminalSubmitMode.Queue, request.SubmitMode);
    }

    [TestMethod]
    public void TerminalInputRejectsInvalidMode()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            TerminalInputParser.Parse(
                $$"""{"type":"prompt","commandId":"command-3","terminalSecret":"{{TerminalSecret}}","prompt":"hello","submitMode":"shell"}"""));
    }

    [TestMethod]
    public void TerminalInputRequiresCommandIdAndSecret()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            TerminalInputParser.Parse(
                "{\"type\":\"prompt\",\"prompt\":\"hello\"}"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            TerminalInputParser.Parse(
                "{\"type\":\"prompt\",\"commandId\":\"command-4\",\"terminalSecret\":\"bad\",\"prompt\":\"hello\"}"));
    }

    [TestMethod]
    public void TerminalCommandResultCacheDeduplicatesCommandId()
    {
        var cache = new TerminalCommandResultCache();
        var input = new TerminalInputRequest(
            "command-cache",
            TerminalSecret,
            "hello",
            TerminalSubmitMode.Steer);
        var injections = 0;

        var first = cache.Execute(input, () => injections++);
        var second = cache.Execute(input, () => injections++);

        Assert.IsTrue(first.Ok);
        Assert.AreEqual(first, second);
        Assert.AreEqual(1, injections);
    }

    [TestMethod]
    public void TerminalCommandResultCacheRejectsChangedContent()
    {
        var cache = new TerminalCommandResultCache();
        var first = new TerminalInputRequest(
            "command-cache",
            TerminalSecret,
            "hello",
            TerminalSubmitMode.Steer);
        var changed = first with { Prompt = "different" };
        var injections = 0;

        Assert.IsTrue(cache.Execute(first, () => injections++).Ok);
        var response = cache.Execute(changed, () => injections++);

        Assert.IsFalse(response.Ok);
        Assert.AreEqual(1, injections);
    }

    [TestMethod]
    public async Task TerminalPipeProtocolRejectsOversizedRequest()
    {
        await using var stream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(new string('a', 33) + "\n"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            TerminalPipeProtocol.ReadLineAsync(
                stream,
                maximumBytes: 32,
                timeout: TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task TerminalPipeProtocolTimesOutIncompleteRequest()
    {
        await using var stream = new NeverCompletingStream();
        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            TerminalPipeProtocol.ReadLineAsync(
                stream,
                timeout: TimeSpan.FromMilliseconds(50)));
    }

    [TestMethod]
    public async Task ManagedTerminalLaunchWaiterRequiresReadyBoundSession()
    {
        var attempts = 0;
        var status = await ManagedTerminalLaunchWaiter.WaitAsync(
            "terminal-ready",
            _ => Task.FromResult<ManagedTerminalLaunchStatus?>(++attempts == 1
                ? new()
                {
                    Ok = true,
                    TerminalId = "terminal-ready",
                    Registered = true,
                    Online = true,
                    Ready = true,
                }
                : new()
                {
                    Ok = true,
                    TerminalId = "terminal-ready",
                    Registered = true,
                    Online = true,
                    Ready = true,
                    SessionExternalId = "session-ready",
                }),
            () => null,
            maximumAttempts: 3,
            pollInterval: TimeSpan.Zero,
            delay: static (_, _) => Task.CompletedTask);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual("session-ready", status.SessionExternalId);
    }

    [TestMethod]
    public async Task ManagedTerminalLaunchWaiterRejectsWrongTerminalAndEarlyExit()
    {
        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            ManagedTerminalLaunchWaiter.WaitAsync(
                "terminal-expected",
                _ => Task.FromResult<ManagedTerminalLaunchStatus?>(new()
                {
                    Ok = true,
                    TerminalId = "terminal-other",
                    Registered = true,
                    Online = true,
                    Ready = true,
                    SessionExternalId = "session-other",
                }),
                () => null,
                maximumAttempts: 1,
                pollInterval: TimeSpan.Zero));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            ManagedTerminalLaunchWaiter.WaitAsync(
                "terminal-expected",
                _ => throw new AssertFailedException("early exit should stop before probing"),
                () => new InvalidOperationException("launcher exited"),
                maximumAttempts: 1));
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
    public async Task ControlTokenReaderPrefersCommittedGenerationOverStaleMirror()
    {
        var bridgeRoot = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-committed-token-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(bridgeRoot, "data");
        var committedToken = new string('a', 64);
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var repository = new BridgeJsonStoreRepository(
                dataDirectory,
                BridgeStoreAccess.ReadWriteActiveOwner);
            await repository.WriteAsync(new(
                new BindingStoreDocument(),
                new SessionStoreDocument(),
                new RouteStoreDocument(),
                new ApprovalStoreDocument(),
                new SettingsStoreDocument(),
                new ControlTokenStoreDocument { Token = committedToken }));
            File.WriteAllText(
                Path.Combine(dataDirectory, "control-token.json"),
                JsonSerializer.Serialize(new { token = new string('b', 64) }));

            Assert.IsTrue(BridgeControlTokenReader.TryRead(bridgeRoot, out var actual));
            Assert.AreEqual(committedToken, actual);
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

    [TestMethod]
    public void BridgeTargetAlwaysSelectsDotNetProduction()
    {
        var production = BridgeHostTarget.FromConfiguration(null, 8765);
        var explicitProduction = BridgeHostTarget.FromConfiguration("dotnet", 9123);

        Assert.IsTrue(production.IsProduction);
        Assert.AreEqual(8765, production.Port);
        Assert.AreEqual("dotnet", production.HostKind);
        Assert.IsTrue(production.ActiveOwner);
        Assert.AreEqual(9123, explicitProduction.Port);
        Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostTarget.FromConfiguration("unsupported", 8765));
    }

    [TestMethod]
    public void BridgeTargetRequiresExactAuthenticatedIdentity()
    {
        var production = BridgeHostTarget.DotNetProduction(8765);
        var status = new BridgeStatus
        {
            ProcessId = 100,
            HostKind = "dotnet",
            ManagementApiVersion = 1,
            InstanceName = BridgeHostTarget.DotNetProductionInstanceName,
            OwnershipMode = "active",
            ActiveOwner = true,
        };
        Assert.IsTrue(production.Matches(status));

        status.HostKind = "unsupported";
        Assert.IsFalse(production.Matches(status));
        status.HostKind = "dotnet";
        status.ActiveOwner = false;
        Assert.IsFalse(production.Matches(status));
    }

    [TestMethod]
    public void BridgeTargetBuildsTheProductionProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-cli-feishu-target-{Guid.NewGuid():N}");
        var application = Path.Combine(root, "app");
        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(application, "AiCliFeishuBridgeHost.exe"), "");
        try
        {
            var production = BridgeHostTarget.DotNetProduction(9123)
                .CreateStartInfo(root, application);
            Assert.AreEqual(
                Path.Combine(application, "AiCliFeishuBridgeHost.exe"),
                production.FileName);
            CollectionAssert.Contains(production.ArgumentList.ToArray(), "9123");
            CollectionAssert.Contains(production.ArgumentList.ToArray(), "active");
            CollectionAssert.Contains(
                production.ArgumentList.ToArray(),
                BridgeHostTarget.DotNetProductionInstanceName);

            Assert.AreEqual("Major", production.Environment["DOTNET_ROLL_FORWARD"]);
            Assert.IsFalse(production.Environment.ContainsKey("BRIDGE_HTTP_PORT"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task BridgeStopWaitsUntilTheExpectedProcessIsOffline()
    {
        var observations = new Queue<BridgeHostExitObservation>([
            BridgeHostExitObservation.Authenticated(400),
            BridgeHostExitObservation.ExpectedProcessAlive,
            BridgeHostExitObservation.Offline,
        ]);
        var calls = 0;

        await BridgeHostExitWaiter.WaitAsync(
            400,
            _ =>
            {
                calls++;
                return Task.FromResult(observations.Dequeue());
            },
            maxAttempts: 3,
            pollInterval: TimeSpan.Zero);

        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task BridgeStopRejectsAReplacementProcess()
    {
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BridgeHostExitWaiter.WaitAsync(
                400,
                _ => Task.FromResult(BridgeHostExitObservation.Authenticated(401)),
                maxAttempts: 1,
                pollInterval: TimeSpan.Zero));

        StringAssert.Contains(error.Message, "pid=400");
        StringAssert.Contains(error.Message, "pid=401");
    }

    [TestMethod]
    public async Task BridgeStopRejectsAnUnauthenticatedEndpoint()
    {
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BridgeHostExitWaiter.WaitAsync(
                400,
                _ => Task.FromResult(BridgeHostExitObservation.Unauthenticated),
                maxAttempts: 1,
                pollInterval: TimeSpan.Zero));

        StringAssert.Contains(error.Message, "无法认证");
    }

    [TestMethod]
    public async Task BridgeStopTimesOutWhileTheExpectedProcessRemainsOnline()
    {
        var calls = 0;
        var error = await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            BridgeHostExitWaiter.WaitAsync(
                400,
                _ =>
                {
                    calls++;
                    return Task.FromResult(BridgeHostExitObservation.Authenticated(400));
                },
                maxAttempts: 3,
                pollInterval: TimeSpan.Zero));

        Assert.AreEqual(3, calls);
        StringAssert.Contains(error.Message, "刷新 Store");
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
