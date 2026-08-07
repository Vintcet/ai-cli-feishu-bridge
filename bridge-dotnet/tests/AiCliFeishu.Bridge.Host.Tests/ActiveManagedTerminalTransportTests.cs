using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveManagedTerminalTransportTests
{
    private static readonly RuntimeCommandContext Context =
        new("command-terminal", "trace-terminal", "correlation-terminal");

    [TestMethod]
    public async Task SendsNormalizedPromptOnlyForCurrentTarget()
    {
        var directory = new RecordingDirectory();
        var pipe = new RecordingTransport();
        var transport = Transport(directory, pipe);
        var target = Target("terminal-current", "session-current");

        await transport.SendAsync(
            Context,
            target,
            "  第一行\r\n\n第二行  ",
            ManagedTerminalSubmitMode.Queue);

        var call = pipe.Calls.Single();
        Assert.AreSame(Context, call.Context);
        Assert.AreSame(target, call.Target);
        Assert.AreEqual("第一行 第二行", call.Prompt);
        Assert.AreEqual(ManagedTerminalSubmitMode.Queue, call.SubmitMode);
        Assert.IsTrue(directory.CurrentChecks >= 2);
    }

    [TestMethod]
    public async Task SerializesSameTerminalWhileDifferentTerminalsProceed()
    {
        var directory = new RecordingDirectory();
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var secondEntered = Signal();
        var otherEntered = Signal();
        var serialCalls = 0;
        var pipe = new RecordingTransport
        {
            Handler = async (call, cancellationToken) =>
            {
                if (call.Target.TerminalId == "terminal-serial")
                {
                    if (Interlocked.Increment(ref serialCalls) == 1)
                    {
                        firstEntered.TrySetResult();
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                    }
                    else
                    {
                        secondEntered.TrySetResult();
                    }
                }
                else
                {
                    otherEntered.TrySetResult();
                }
            },
        };
        var transport = Transport(directory, pipe);
        var serialTarget = Target("terminal-serial", "session-serial");

        var first = transport.SendAsync(
            Context,
            serialTarget,
            "first",
            ManagedTerminalSubmitMode.Steer);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = transport.SendAsync(
            Context,
            serialTarget,
            "second",
            ManagedTerminalSubmitMode.Steer);
        var other = transport.SendAsync(
            Context,
            Target("terminal-other", "session-other"),
            "other",
            ManagedTerminalSubmitMode.Steer);

        await otherEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(secondEntered.Task.IsCompleted);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second, other);
        Assert.IsTrue(secondEntered.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task RetriesOnlyUnavailableFailuresWithNodeBackoff()
    {
        var directory = new RecordingDirectory();
        var attempts = 0;
        var pipe = new RecordingTransport
        {
            Handler = (_, _) => ++attempts < 3
                ? Task.FromException(new ManagedTerminalUnavailableException("offline"))
                : Task.CompletedTask,
        };
        var delays = new List<TimeSpan>();
        var transport = Transport(
            directory,
            pipe,
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        await transport.SendAsync(
            Context,
            Target(),
            "retry",
            ManagedTerminalSubmitMode.Steer);

        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300) },
            delays);
    }

    [TestMethod]
    public async Task StopsAfterFourUnavailableAttempts()
    {
        var directory = new RecordingDirectory();
        var attempts = 0;
        var pipe = new RecordingTransport
        {
            Handler = (_, _) =>
            {
                attempts++;
                return Task.FromException(
                    new ManagedTerminalUnavailableException($"offline-{attempts}"));
            },
        };
        var delays = new List<TimeSpan>();
        var transport = Transport(
            directory,
            pipe,
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        var error = await Assert.ThrowsExceptionAsync<ManagedTerminalUnavailableException>(() =>
            transport.SendAsync(
                Context,
                Target(),
                "retry",
                ManagedTerminalSubmitMode.Steer));

        Assert.AreEqual("offline-4", error.Message);
        Assert.AreEqual(4, attempts);
        CollectionAssert.AreEqual(
            new[]
            {
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(450),
            },
            delays);
    }

    [TestMethod]
    public async Task DoesNotRetryTerminalRejectionOrMalformedResponse()
    {
        foreach (var expected in new Exception[]
                 {
                     new ManagedTerminalRejectedException("terminal rejected"),
                     new JsonException("malformed response"),
                     new InvalidOperationException("identity replaced"),
                 })
        {
            var attempts = 0;
            var delays = 0;
            var pipe = new RecordingTransport
            {
                Handler = (_, _) =>
                {
                    attempts++;
                    return Task.FromException(expected);
                },
            };
            var transport = Transport(
                new RecordingDirectory(),
                pipe,
                (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                });

            var actual = await CaptureAsync(() => transport.SendAsync(
                Context,
                Target(),
                "do not retry",
                ManagedTerminalSubmitMode.Steer));

            Assert.AreSame(expected, actual);
            Assert.AreEqual(1, attempts);
            Assert.AreEqual(0, delays);
        }
    }

    [TestMethod]
    public async Task RechecksIdentityBeforeRetrying()
    {
        var current = true;
        var directory = new RecordingDirectory
        {
            Handler = _ => current,
        };
        var pipe = new RecordingTransport
        {
            Handler = (_, _) => Task.FromException(
                new ManagedTerminalUnavailableException("offline")),
        };
        var transport = Transport(
            directory,
            pipe,
            (_, _) =>
            {
                current = false;
                return Task.CompletedTask;
            });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            transport.SendAsync(
                Context,
                Target(),
                "identity changes",
                ManagedTerminalSubmitMode.Steer));

        Assert.AreEqual(1, pipe.Calls.Count);
    }

    [TestMethod]
    public async Task RejectsQueuedTargetAfterGenerationReplacement()
    {
        var currentGeneration = 1L;
        var directory = new RecordingDirectory
        {
            Handler = target => target.Generation == currentGeneration,
        };
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var pipe = new RecordingTransport
        {
            Handler = async (_, cancellationToken) =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            },
        };
        var transport = Transport(directory, pipe);
        var original = Target(generation: 1);

        var first = transport.SendAsync(
            Context,
            original,
            "first",
            ManagedTerminalSubmitMode.Steer);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = transport.SendAsync(
            Context,
            original,
            "queued",
            ManagedTerminalSubmitMode.Steer);
        currentGeneration = 2;
        releaseFirst.TrySetResult();

        await first;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => queued);
        Assert.AreEqual(1, pipe.Calls.Count);
    }

    [TestMethod]
    public async Task QueuedCancellationDoesNotReachPipeOrReleaseCurrentSender()
    {
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var thirdEntered = Signal();
        var calls = 0;
        var pipe = new RecordingTransport
        {
            Handler = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    thirdEntered.TrySetResult();
                }
            },
        };
        var transport = Transport(new RecordingDirectory(), pipe);
        var target = Target();
        var first = transport.SendAsync(
            Context,
            target,
            "first",
            ManagedTerminalSubmitMode.Steer);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var canceled = transport.SendAsync(
            Context,
            target,
            "canceled",
            ManagedTerminalSubmitMode.Steer,
            cancellation.Token);
        var third = transport.SendAsync(
            Context,
            target,
            "third",
            ManagedTerminalSubmitMode.Steer);

        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => canceled);
        Assert.AreEqual(1, pipe.Calls.Count);
        Assert.IsFalse(thirdEntered.Task.IsCompleted);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, third);
        Assert.AreEqual(2, pipe.Calls.Count);
    }

    [TestMethod]
    public async Task FailsClosedForPassiveInvalidOrStaleTargets()
    {
        var directory = new RecordingDirectory();
        var pipe = new RecordingTransport();
        var passive = new ActiveManagedTerminalTransport(
            BridgeHostOptions.Passive(Path.GetTempPath(), port: 0),
            directory,
            pipe,
            NoDelay);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.SendAsync(
                Context,
                Target(),
                "passive",
                ManagedTerminalSubmitMode.Steer));
        Assert.AreEqual(0, directory.CurrentChecks);

        var active = Transport(directory, pipe);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            active.SendAsync(
                Context,
                Target() with { Ready = false },
                "not ready",
                ManagedTerminalSubmitMode.Steer));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            active.SendAsync(
                Context,
                Target() with { Generation = 0 },
                "legacy target",
                ManagedTerminalSubmitMode.Steer));
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            active.SendAsync(
                Context,
                Target(),
                "invalid mode",
                (ManagedTerminalSubmitMode)42));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            active.SendAsync(
                Context,
                Target(),
                " \r\n ",
                ManagedTerminalSubmitMode.Steer));

        directory.Handler = _ => false;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            active.SendAsync(
                Context,
                Target(),
                "stale",
                ManagedTerminalSubmitMode.Steer));
        Assert.AreEqual(0, pipe.Calls.Count);
    }

    private static ActiveManagedTerminalTransport Transport(
        RecordingDirectory directory,
        IManagedTerminalTransport pipe,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
            ActiveOptions(),
            directory,
            pipe,
            delay ?? NoDelay);

    private static BridgeHostOptions ActiveOptions() => new(
        Path.Combine(Path.GetTempPath(), "active-terminal-transport-tests"),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "active-terminal-transport-test");

    private static ManagedTerminalTarget Target(
        string terminalId = "terminal-active",
        string sessionId = "session-active",
        long generation = 1) => new(
            terminalId,
            sessionId,
            Ready: true,
            generation);

    private static Task NoDelay(TimeSpan _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            throw new AssertFailedException("预期传输调用失败。");
        }
        catch (Exception error) when (error is not AssertFailedException)
        {
            return error;
        }
    }

    private sealed record TransportCall(
        RuntimeCommandContext Context,
        ManagedTerminalTarget Target,
        string Prompt,
        ManagedTerminalSubmitMode SubmitMode);

    private sealed class RecordingTransport : IManagedTerminalTransport
    {
        private readonly ConcurrentQueue<TransportCall> calls = new();

        public IReadOnlyCollection<TransportCall> Calls => calls.ToArray();

        public Func<TransportCall, CancellationToken, Task> Handler { get; set; } =
            static (_, _) => Task.CompletedTask;

        public Task SendAsync(
            RuntimeCommandContext context,
            ManagedTerminalTarget target,
            string prompt,
            ManagedTerminalSubmitMode submitMode,
            CancellationToken cancellationToken = default)
        {
            var call = new TransportCall(context, target, prompt, submitMode);
            calls.Enqueue(call);
            return Handler(call, cancellationToken);
        }
    }

    private sealed class RecordingDirectory
        : IBridgeManagedTerminalRegistrationDirectory
    {
        private int currentChecks;

        public BridgeManagedTerminalDirectorySnapshot Snapshot { get; } =
            new(true, 0, 0, 0, 0);

        public int CurrentChecks => Volatile.Read(ref currentChecks);

        public Func<ManagedTerminalTarget, bool> Handler { get; set; } =
            static _ => true;

        public void Register(BridgeManagedTerminalRegistration registration) { }
        public bool Unregister(string terminalId) => false;
        public BridgeManagedTerminalClaim? Claim(
            string cwd,
            string runtime,
            string sessionExternalId) => null;
        public BridgeManagedTerminalClaim? ClaimById(
            string terminalId,
            string cwd,
            string runtime,
            string sessionExternalId,
            bool? elevated = null) => null;
        public BridgeManagedTerminalIdentity? FindClaimBySession(
            string sessionExternalId) => null;
        public BridgeManagedTerminalIdentity? FindClaimByTerminal(
            string terminalId) => null;
        public void Release(string sessionExternalId) { }

        public bool IsCurrent(ManagedTerminalTarget target)
        {
            Interlocked.Increment(ref currentChecks);
            return Handler(target);
        }
    }
}
