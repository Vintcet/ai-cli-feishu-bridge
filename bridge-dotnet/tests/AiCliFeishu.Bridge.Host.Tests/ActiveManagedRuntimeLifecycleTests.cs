using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveManagedRuntimeLifecycleTests
{
    private static readonly DateTimeOffset Origin =
        DateTimeOffset.Parse("2026-08-07T08:00:00.000Z");

    [TestMethod]
    public async Task PublishesAndClaimsOldestLaunchExactlyOnce()
    {
        var clock = new MutableTimeProvider(Origin);
        using var lifecycle = Lifecycle(Store(), clock: clock);

        await lifecycle.LaunchAsync(
            Context("first"),
            RuntimeNames.Codex,
            "session-first",
            ProjectPath("first"),
            prompt: null,
            elevated: false);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await lifecycle.LaunchAsync(
            Context("second"),
            RuntimeNames.ClaudeCode,
            "session-second",
            ProjectPath("second"),
            prompt: null,
            elevated: true);

        var first = lifecycle.Claim();
        var second = lifecycle.Claim();
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual("session-first", first.SessionId);
        Assert.AreEqual("new", first.Kind);
        Assert.AreEqual("first", first.ProjectName);
        Assert.AreEqual("session-second", second.SessionId);
        Assert.IsTrue(second.Elevated);
        Assert.IsNull(lifecycle.Claim());

        var completed = lifecycle.Complete(new(first.RequestId, true, null));
        Assert.IsTrue(completed.Ok);
        Assert.AreEqual("new", completed.Kind);
        var duplicate = lifecycle.Complete(new(first.RequestId, true, null));
        Assert.IsTrue(duplicate.Ok);
        Assert.IsTrue(duplicate.AlreadyResolved);
        Assert.AreEqual(
            new BridgeManagedRuntimeLifecycleSnapshot(0, 1, 1, 0),
            lifecycle.Snapshot);
    }

    [TestMethod]
    public async Task ResumeUsesPersistedIdentityAndDrainsPromptsInOrder()
    {
        var directory = new RecordingDirectory();
        var transport = new RecordingTransport();
        var cwd = ProjectPath("resume");
        using var lifecycle = Lifecycle(
            Store(Session(
                "session-resume",
                cwd,
                RuntimeNames.Codex,
                "waiting",
                ("managedTerminalElevated", true))),
            directory,
            transport);

        await lifecycle.ResumeAsync(
            Context("resume-one"),
            RuntimeNames.Codex,
            "session-resume",
            OperatingSystem.IsWindows() ? cwd.ToUpperInvariant() : cwd,
            "第一条");
        await lifecycle.ResumeAsync(
            Context("resume-two"),
            RuntimeNames.Codex,
            "session-resume",
            cwd,
            "第二条");

        var request = lifecycle.Claim();
        Assert.IsNotNull(request);
        Assert.AreEqual("resume", request.Kind);
        Assert.IsTrue(request.Elevated);
        Assert.IsNull(lifecycle.Claim());
        Assert.IsTrue(lifecycle.Complete(new(request.RequestId, true, null)).Ok);
        Assert.AreEqual(
            new BridgeManagedRuntimeLifecycleSnapshot(0, 0, 1, 2),
            lifecycle.Snapshot);

        directory.Target = new(
            "terminal-resume",
            "session-resume",
            Ready: true,
            Generation: 1);
        await lifecycle.DrainAsync("session-resume");

        Assert.AreEqual(2, transport.Calls.Count);
        Assert.AreEqual("第一条", transport.Calls[0].Prompt);
        Assert.AreEqual(ManagedTerminalSubmitMode.Steer, transport.Calls[0].Mode);
        Assert.AreEqual("第二条", transport.Calls[1].Prompt);
        Assert.AreEqual(ManagedTerminalSubmitMode.Queue, transport.Calls[1].Mode);
        Assert.AreEqual(
            new BridgeManagedRuntimeLifecycleSnapshot(0, 0, 0, 0),
            lifecycle.Snapshot);
    }

    [TestMethod]
    public async Task ResumeFailsClosedForMissingEndedOrConflictingStoreSessions()
    {
        var cwd = ProjectPath("identity");
        using var missing = Lifecycle(Store());
        await Assert.ThrowsExceptionAsync<KeyNotFoundException>(() =>
            missing.ResumeAsync(
                Context("missing"),
                RuntimeNames.Codex,
                "session-missing",
                cwd,
                prompt: null));

        using var ended = Lifecycle(Store(Session(
            "session-ended",
            cwd,
            RuntimeNames.Codex,
            "ended")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            ended.ResumeAsync(
                Context("ended"),
                RuntimeNames.Codex,
                "session-ended",
                cwd,
                prompt: null));

        using var conflicting = Lifecycle(Store(Session(
            "session-conflict",
            cwd,
            RuntimeNames.ClaudeCode,
            "waiting")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            conflicting.ResumeAsync(
                Context("conflict"),
                RuntimeNames.Codex,
                "session-conflict",
                cwd,
                prompt: null));
        Assert.AreEqual(
            new BridgeManagedRuntimeLifecycleSnapshot(0, 0, 0, 0),
            conflicting.Snapshot);
    }

    [TestMethod]
    public async Task ExpirationRemovesClaimedRequestsAndQueuedPrompts()
    {
        var clock = new MutableTimeProvider(Origin);
        using var lifecycle = Lifecycle(Store(), clock: clock);
        await lifecycle.LaunchAsync(
            Context("expire"),
            RuntimeNames.Codex,
            "session-expire",
            ProjectPath("expire"),
            "稍后发送",
            elevated: false);
        var request = lifecycle.Claim();
        Assert.IsNotNull(request);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.IsNull(lifecycle.Claim());
        Assert.AreEqual(
            new BridgeManagedRuntimeLifecycleSnapshot(0, 0, 0, 0),
            lifecycle.Snapshot);
        var late = lifecycle.Complete(new(request.RequestId, true, null));
        Assert.IsTrue(late.Ok);
        Assert.IsTrue(late.AlreadyResolved);
    }

    [TestMethod]
    public async Task CompletionRequiresClaimAndFailureClearsRequest()
    {
        using var lifecycle = Lifecycle(
            Store(),
            requestIdFactory: static () => "request-complete");
        await lifecycle.LaunchAsync(
            Context("complete"),
            RuntimeNames.Codex,
            "session-complete",
            ProjectPath("complete"),
            "不会发送",
            elevated: false);
        var premature = lifecycle.Complete(new("request-complete", true, null));
        Assert.IsFalse(premature.Ok);
        Assert.AreEqual(1, lifecycle.Snapshot.Pending);
        var request = lifecycle.Claim();
        Assert.IsNotNull(request);
        var failed = lifecycle.Complete(new(request.RequestId, false, "启动失败"));
        Assert.IsTrue(failed.Ok);
        Assert.AreEqual("session-complete", failed.SessionId);
        Assert.AreEqual("启动失败", failed.FailureDetail);
        Assert.AreEqual(
            new BridgeManagedRuntimeLifecycleSnapshot(0, 0, 0, 0),
            lifecycle.Snapshot);
    }

    [TestMethod]
    public async Task StopCancelsOnlyRequestsThatDesktopHasNotClaimed()
    {
        var directory = new RecordingDirectory();
        using var lifecycle = Lifecycle(Store(), directory);
        await lifecycle.LaunchAsync(
            Context("cancel-pending"),
            RuntimeNames.Codex,
            "session-pending",
            ProjectPath("pending"),
            prompt: null,
            elevated: false);

        await lifecycle.StopAsync(
            Context("stop-pending"),
            RuntimeNames.Codex,
            "session-pending",
            "撤销");
        Assert.IsNull(lifecycle.Claim());

        await lifecycle.LaunchAsync(
            Context("claim-first"),
            RuntimeNames.Codex,
            "session-claimed",
            ProjectPath("claimed"),
            prompt: null,
            elevated: false);
        _ = lifecycle.Claim();
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            lifecycle.StopAsync(
                Context("stop-claimed"),
                RuntimeNames.Codex,
                "session-claimed",
                reason: null));

        directory.Target = new(
            "terminal-online",
            "session-online",
            Ready: true,
            Generation: 1);
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            lifecycle.StopAsync(
                Context("stop-online"),
                RuntimeNames.Codex,
                "session-online",
                reason: null));
    }

    [TestMethod]
    public async Task FailedDrainRestoresUnsentPrompts()
    {
        var directory = new RecordingDirectory
        {
            Target = new(
                "terminal-retry",
                "session-retry",
                Ready: true,
                Generation: 1),
        };
        var attempts = 0;
        var transport = new RecordingTransport
        {
            Handler = _ => ++attempts == 1
                ? Task.FromException(new IOException("test failure"))
                : Task.CompletedTask,
        };
        var cwd = ProjectPath("retry");
        using var lifecycle = Lifecycle(
            Store(Session("session-retry", cwd, RuntimeNames.Codex, "waiting")),
            directory,
            transport);
        await lifecycle.ResumeAsync(
            Context("retry-one"),
            RuntimeNames.Codex,
            "session-retry",
            cwd,
            "first");
        await lifecycle.ResumeAsync(
            Context("retry-two"),
            RuntimeNames.Codex,
            "session-retry",
            cwd,
            "second");
        var request = lifecycle.Claim();
        Assert.IsNotNull(request);
        _ = lifecycle.Complete(new(request.RequestId, true, null));

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            lifecycle.DrainAsync("session-retry"));
        Assert.AreEqual(2, lifecycle.Snapshot.QueuedPrompts);
        await lifecycle.DrainAsync("session-retry");

        CollectionAssert.AreEqual(
            new[] { "first", "first", "second" },
            transport.Calls.Select(call => call.Prompt).ToArray());
        Assert.AreEqual(0, lifecycle.Snapshot.QueuedPrompts);
    }

    [TestMethod]
    public async Task ConcurrentClaimsNeverReturnTheSameRequest()
    {
        using var lifecycle = Lifecycle(Store());
        for (var index = 0; index < 40; index++)
        {
            await lifecycle.LaunchAsync(
                Context($"parallel-{index}"),
                RuntimeNames.Codex,
                $"session-parallel-{index}",
                ProjectPath($"parallel-{index}"),
                prompt: null,
                elevated: false);
        }
        var claimed = new ConcurrentBag<string>();

        Parallel.For(0, 80, _ =>
        {
            if (lifecycle.Claim() is { } request)
            {
                claimed.Add(request.RequestId);
            }
        });

        Assert.AreEqual(40, claimed.Count);
        Assert.AreEqual(40, claimed.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public async Task RejectsPassiveOwnershipAndPreCanceledPublication()
    {
        var passive = new ActiveManagedRuntimeLifecycle(
            BridgeHostOptions.Passive(Path.GetTempPath(), port: 0),
            new RecordingStoreOwner(Store()),
            new RecordingDirectory(),
            new RecordingTransport());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.LaunchAsync(
                Context("passive"),
                RuntimeNames.Codex,
                "session-passive",
                ProjectPath("passive"),
                prompt: null,
                elevated: false));
        passive.Dispose();

        using var active = Lifecycle(Store());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            active.LaunchAsync(
                Context("canceled"),
                RuntimeNames.Codex,
                "session-canceled",
                ProjectPath("canceled"),
                prompt: null,
                elevated: false,
                cancellation.Token));
        Assert.AreEqual(0, active.Snapshot.Pending);
    }

    [TestMethod]
    public void TheRequestLifetimeOutlivesTheDesktopLaunchWait()
    {
        // A claimed request holds the queued Feishu prompt, so expiring it before the
        // desktop panel gives up polling would silently drop that prompt.
        Assert.IsTrue(
            ActiveManagedRuntimeLifecycle.DefaultRequestLifetime >
            ActiveManagedRuntimeLifecycle.DesktopLaunchWait,
            $"Request lifetime {ActiveManagedRuntimeLifecycle.DefaultRequestLifetime} must " +
            $"outlive the desktop launch wait {ActiveManagedRuntimeLifecycle.DesktopLaunchWait}.");
    }

    private static ActiveManagedRuntimeLifecycle Lifecycle(
        BridgeStoreSnapshot store,
        RecordingDirectory? directory = null,
        RecordingTransport? transport = null,
        MutableTimeProvider? clock = null,
        Func<string>? requestIdFactory = null) => new(
            ActiveOptions(),
            new RecordingStoreOwner(store),
            directory ?? new RecordingDirectory(),
            transport ?? new RecordingTransport(),
            clock ?? new MutableTimeProvider(Origin),
            TimeSpan.FromMinutes(2),
            requestIdFactory);

    private static BridgeHostOptions ActiveOptions() => new(
        ProjectPath("data"),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "managed-runtime-lifecycle-test");

    private static RuntimeCommandContext Context(string id) =>
        new($"command-{id}", $"trace-{id}", $"correlation-{id}");

    private static string ProjectPath(string name) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bridge-runtime-tests", name));

    private static BridgeStoreSnapshot Store(params SessionStoreRecord[] sessions)
    {
        var sessionDocument = new SessionStoreDocument();
        foreach (var session in sessions)
        {
            sessionDocument.Sessions[session.SessionId] = session;
        }
        return new(
            new BindingStoreDocument(),
            sessionDocument,
            new RouteStoreDocument(),
            new ApprovalStoreDocument(),
            new SettingsStoreDocument(),
            new ControlTokenStoreDocument());
    }

    private static SessionStoreRecord Session(
        string sessionId,
        string cwd,
        string runtime,
        string status,
        params (string Name, object? Value)[] extensions) => new()
        {
            SessionId = sessionId,
            Cwd = cwd,
            Runtime = runtime,
            Status = status,
            ProjectName = Path.GetFileName(cwd),
            LastSeenAt = Origin.ToString("O"),
            ExtensionData = extensions.ToDictionary(
                item => item.Name,
                item => JsonSerializer.SerializeToElement(item.Value),
                StringComparer.Ordinal),
        };

    private sealed class RecordingStoreOwner(BridgeStoreSnapshot store)
        : IBridgeProductionStoreOwner
    {
        public BridgeProductionStoreSnapshot Snapshot => new(
            BridgeProductionStoreState.Open,
            null,
            6);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(store);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<BridgeStoreSnapshot, BridgeStoreSnapshot> update,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingDirectory : IManagedTerminalDirectory
    {
        public ManagedTerminalTarget? Target { get; set; }

        public ManagedTerminalTarget? FindBySession(string sessionExternalId) =>
            Target is not null && string.Equals(
                Target.SessionExternalId,
                sessionExternalId,
                StringComparison.Ordinal)
                ? Target
                : null;
    }

    private sealed class RecordingTransport : IManagedTerminalTransport
    {
        public List<TransportCall> Calls { get; } = [];
        public Func<TransportCall, Task>? Handler { get; set; }

        public Task SendAsync(
            RuntimeCommandContext context,
            ManagedTerminalTarget target,
            string prompt,
            ManagedTerminalSubmitMode submitMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new TransportCall(context, target, prompt, submitMode);
            Calls.Add(call);
            return Handler?.Invoke(call) ?? Task.CompletedTask;
        }
    }

    private sealed record TransportCall(
        RuntimeCommandContext Context,
        ManagedTerminalTarget Target,
        string Prompt,
        ManagedTerminalSubmitMode Mode);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }
}
