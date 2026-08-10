using System.Net;
using System.Text;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class CodexTranscriptMonitorTests
{
    [TestMethod]
    public async Task IgnoresHistoryAndParsesNewSegmentedUtf8Error()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-transcript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(
            path,
            "{\"type\":\"task_complete\",\"turn_id\":\"old\",\"error\":\"old error\"}\n");
        var events = new List<CodexTranscriptErrorEvent>();
        var monitor = new CodexTranscriptMonitor(new(
            directory,
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "transcript-test"));
        monitor.Attach((value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        });
        try
        {
            Assert.IsTrue(await monitor.WatchAsync("session-1", path));
            Assert.AreEqual("watches=1", monitor.ComponentHealth.Detail);
            await monitor.StartAsync(CancellationToken.None);
            var line = Encoding.UTF8.GetBytes(
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-new\",\"error\":{\"message\":\"服务暂时不可用\",\"code\":\"upstream_unavailable\"}}}\n");
            var split = line.Length - 5;
            await using (var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite))
            {
                await stream.WriteAsync(line.AsMemory(0, split));
                await stream.FlushAsync();
            }
            await monitor.CheckNowAsync();
            Assert.AreEqual(0, events.Count);

            await using (var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite))
            {
                await stream.WriteAsync(line.AsMemory(split));
                await stream.FlushAsync();
            }
            await monitor.CheckNowAsync();

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("session-1", events[0].SessionId);
            Assert.AreEqual("turn-new", events[0].TurnId);
            Assert.AreEqual("服务暂时不可用", events[0].Error);
            Assert.AreEqual("upstream_unavailable", events[0].ErrorCode);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MalformedLineDoesNotBlockLaterValidError()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-transcript-malformed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(path, "");
        var events = new List<CodexTranscriptErrorEvent>();
        var monitor = CreateMonitor(directory, events);
        try
        {
            Assert.IsTrue(await monitor.WatchAsync("session-malformed", path));
            await monitor.StartAsync(CancellationToken.None);
            await File.AppendAllTextAsync(
                path,
                "{not-json}\n" +
                "{\"type\":\"task_complete\",\"turn_id\":\"turn-valid\",\"error\":\"HTTP 503\"}\n");

            await monitor.CheckNowAsync();

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("turn-valid", events[0].TurnId);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedHandlerLeavesLineUncommittedForNextPoll()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-transcript-handler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(path, "");
        var attempts = 0;
        var events = new List<CodexTranscriptErrorEvent>();
        var monitor = new CodexTranscriptMonitor(Options(directory));
        monitor.Attach((value, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new IOException("synthetic cursor failure");
            }
            events.Add(value);
            return Task.CompletedTask;
        });
        try
        {
            Assert.IsTrue(await monitor.WatchAsync("session-handler", path));
            await monitor.StartAsync(CancellationToken.None);
            await File.AppendAllTextAsync(
                path,
                "{\"type\":\"task_complete\",\"turn_id\":\"turn-retry\",\"error\":\"HTTP 502\"}\n");

            await monitor.CheckNowAsync();
            Assert.AreEqual(1, attempts);
            Assert.AreEqual(0, events.Count);

            await monitor.CheckNowAsync();
            Assert.AreEqual(2, attempts);
            Assert.AreEqual("turn-retry", events.Single().TurnId);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task HandlerFailureCommitsOnlyTheSuccessfulBatchPrefix()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-transcript-prefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(path, "");
        var attempts = new List<string>();
        var failSecondOnce = true;
        var monitor = new CodexTranscriptMonitor(Options(directory));
        monitor.Attach((value, _) =>
        {
            attempts.Add(value.TurnId);
            if (value.TurnId == "turn-2" && failSecondOnce)
            {
                failSecondOnce = false;
                throw new IOException("synthetic handler failure");
            }
            return Task.CompletedTask;
        });
        try
        {
            Assert.IsTrue(await monitor.WatchAsync("session-prefix", path));
            await monitor.StartAsync(CancellationToken.None);
            await File.AppendAllTextAsync(
                path,
                "{\"type\":\"task_complete\",\"turn_id\":\"turn-1\",\"error\":\"HTTP 502\"}\n" +
                "{\"type\":\"task_complete\",\"turn_id\":\"turn-2\",\"error\":\"HTTP 503\"}\n");

            await monitor.CheckNowAsync();
            await monitor.CheckNowAsync();

            CollectionAssert.AreEqual(
                new[] { "turn-1", "turn-2", "turn-2" },
                attempts);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CorruptedCursorFailsClosedAndReplaysExistingTranscript()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-transcript-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(
            path,
            "{\"type\":\"task_complete\",\"turn_id\":\"must-replay\",\"error\":\"HTTP 503\"}\n");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "codex-transcript-cursors.json"),
            "{not-json}");
        var events = new List<CodexTranscriptErrorEvent>();
        var monitor = CreateMonitor(directory, events);
        try
        {
            Assert.IsTrue(await monitor.WatchAsync("session-corrupt", path));
            await monitor.StartAsync(CancellationToken.None);
            await monitor.CheckNowAsync();

            Assert.AreEqual("must-replay", events.Single().TurnId);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExistingSessionResumesDurableCursorAfterHostRestart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-transcript-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        await File.WriteAllTextAsync(
            path,
            "{\"type\":\"task_complete\",\"turn_id\":\"old\",\"error\":\"old\"}\n");

        try
        {
            var firstEvents = new List<CodexTranscriptErrorEvent>();
            var first = CreateMonitor(directory, firstEvents);
            try
            {
                Assert.IsTrue(await first.WatchAsync("session-restart", path));
                await first.StartAsync(CancellationToken.None);
            }
            finally
            {
                await first.StopAsync(CancellationToken.None);
            }

            await File.AppendAllTextAsync(
                path,
                "{\"type\":\"task_complete\",\"turn_id\":\"during-restart\",\"error\":\"HTTP 503\"}\n");

            var recoveredEvents = new List<CodexTranscriptErrorEvent>();
            var recovered = CreateMonitor(directory, recoveredEvents);
            try
            {
                Assert.IsTrue(await recovered.WatchAsync("session-restart", path));
                await recovered.StartAsync(CancellationToken.None);
                await recovered.CheckNowAsync();

                Assert.AreEqual(0, firstEvents.Count);
                Assert.AreEqual("during-restart", recoveredEvents.Single().TurnId);
            }
            finally
            {
                await recovered.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CodexTranscriptMonitor CreateMonitor(
        string directory,
        ICollection<CodexTranscriptErrorEvent> events)
    {
        var monitor = new CodexTranscriptMonitor(Options(directory));
        monitor.Attach((value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        });
        return monitor;
    }

    private static BridgeHostOptions Options(string directory) => new(
        directory,
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "transcript-test");
}
