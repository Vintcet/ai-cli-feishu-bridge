using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveOpenCodeRuntimeLifecycleTests
{
    private const string SessionId = "session-opencode-lifecycle";
    private static readonly RuntimeCommandContext Context =
        new("command-lifecycle", "trace-lifecycle", "correlation-lifecycle");
    private static readonly string Cwd = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "opencode-lifecycle-project"));

    [TestMethod]
    public async Task LaunchResumeAndPendingStopUseSharedDesktopQueueAsOpenCode()
    {
        var directory = await DirectoryAsync();
        var desktop = new RecordingDesktopLifecycle();
        using var lifecycle = Lifecycle(directory, desktop, new QueuePortAllocator());

        await lifecycle.LaunchAsync(Context, "session-launch", Cwd, elevated: true);
        await lifecycle.ResumeAsync(Context, "session-resume", Cwd);
        await lifecycle.StopAsync(Context, "session-pending", "cancel");

        Assert.AreEqual(3, desktop.Calls.Count);
        Assert.IsTrue(desktop.Calls.All(call => call.Runtime == RuntimeNames.OpenCode));
        Assert.AreEqual("launch", desktop.Calls[0].Operation);
        Assert.AreEqual("session-launch", desktop.Calls[0].SessionId);
        Assert.AreEqual(Cwd, desktop.Calls[0].Cwd);
        Assert.IsTrue(desktop.Calls[0].Elevated);
        Assert.IsNull(desktop.Calls[0].Prompt);
        Assert.AreEqual("resume", desktop.Calls[1].Operation);
        Assert.AreEqual("session-resume", desktop.Calls[1].SessionId);
        Assert.AreEqual("stop", desktop.Calls[2].Operation);
        Assert.AreEqual("cancel", desktop.Calls[2].Reason);
    }

    [TestMethod]
    public async Task ReservationExcludesKnownPortsAndPrebindsResumeSession()
    {
        var directory = await DirectoryAsync();
        directory.Register(5_100, Cwd);
        var allocator = new QueuePortAllocator(5_101);
        using var lifecycle = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            allocator);

        var identity = await lifecycle.ReserveAsync(Cwd, SessionId);

        Assert.AreEqual(5_101, identity.Port);
        Assert.IsFalse(identity.Ready);
        CollectionAssert.Contains(
            allocator.Exclusions.Single().ToArray(),
            5_100);
        var target = directory.FindRegistrationBySession(SessionId);
        Assert.IsNotNull(target);
        Assert.AreEqual(identity.Generation, target.Generation);
        Assert.IsTrue(lifecycle.Release(identity.Port));
        Assert.IsNull(directory.FindRegistrationBySession(SessionId));
    }

    [TestMethod]
    public async Task ConcurrentReservationsAreSerializedAndUseDifferentPorts()
    {
        var directory = await DirectoryAsync();
        var allocator = new QueuePortAllocator(5_102, 5_103);
        using var lifecycle = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            allocator);

        var reservations = await Task.WhenAll(
            lifecycle.ReserveAsync(Cwd, sessionExternalId: null).AsTask(),
            lifecycle.ReserveAsync(Cwd, sessionExternalId: null).AsTask());

        CollectionAssert.AreEquivalent(
            new[] { 5_102, 5_103 },
            reservations.Select(identity => identity.Port).ToArray());
        Assert.AreEqual(1, allocator.MaximumConcurrency);
    }

    [TestMethod]
    public async Task StaleReservationReleaseCannotRemoveReplacementGeneration()
    {
        var directory = await DirectoryAsync();
        using var lifecycle = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            new QueuePortAllocator(5_104));
        var reserved = await lifecycle.ReserveAsync(Cwd, SessionId);
        var replacement = directory.Register(
            reserved.Port,
            Path.Combine(Cwd, "replacement"));

        Assert.IsFalse(lifecycle.Release(reserved.Port));

        var current = directory.ListRegistrations().Single();
        Assert.AreEqual(replacement.Generation, current.Generation);
        Assert.AreEqual(replacement.Cwd, current.Cwd);
    }

    [TestMethod]
    public async Task ReleaseWithoutReservationUnregistersExternalEndpoint()
    {
        var directory = await DirectoryAsync();
        var external = directory.Register(5_109, Cwd);
        using var lifecycle = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            new QueuePortAllocator());

        Assert.IsTrue(lifecycle.Release(external.Port));
        Assert.AreEqual(0, directory.ListRegistrations().Count);
    }

    [TestMethod]
    public async Task WaitReturnsOnlyAfterMappedGenerationIsReady()
    {
        var directory = await DirectoryAsync();
        using var lifecycle = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            new QueuePortAllocator(5_105),
            delay: (_, cancellationToken) =>
            {
                var target = directory.FindRegistrationBySession(SessionId)!;
                Assert.IsTrue(directory.SetReady(
                    target.Port,
                    target.Generation,
                    ready: true));
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var reserved = await lifecycle.ReserveAsync(Cwd, SessionId);

        await lifecycle.WaitUntilReadyAsync(Context, SessionId);

        var ready = directory.FindRegistrationBySession(SessionId);
        Assert.IsNotNull(ready);
        Assert.AreEqual(reserved.Generation, ready.Generation);
        Assert.IsTrue(directory.IsCurrent(ready, SessionId));
    }

    [TestMethod]
    public async Task ReadyWaitDistinguishesTimeoutCallerCancellationAndDisposal()
    {
        var directory = await DirectoryAsync();
        using var timeout = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            new QueuePortAllocator(),
            readyTimeout: TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            timeout.WaitUntilReadyAsync(Context, SessionId));

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            timeout.WaitUntilReadyAsync(
                Context,
                SessionId,
                callerCancellation.Token));

        var disposed = Lifecycle(
            directory,
            new RecordingDesktopLifecycle(),
            new QueuePortAllocator(),
            readyTimeout: TimeSpan.FromSeconds(2));
        var waiting = disposed.WaitUntilReadyAsync(Context, SessionId);
        disposed.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => waiting);
    }

    [TestMethod]
    public async Task StopForgetsReadySessionWithoutClaimingProcessOwnership()
    {
        var directory = await DirectoryAsync();
        var desktop = new RecordingDesktopLifecycle();
        using var lifecycle = Lifecycle(
            directory,
            desktop,
            new QueuePortAllocator(5_106));
        var target = await lifecycle.ReserveAsync(Cwd, SessionId);
        directory.SetReady(target.Port, target.Generation, ready: true);

        await lifecycle.StopAsync(Context, SessionId, "done");

        Assert.IsNull(directory.FindRegistrationBySession(SessionId));
        Assert.AreEqual(1, directory.ListRegistrations().Count);
        Assert.AreEqual(0, desktop.Calls.Count);
    }

    [TestMethod]
    public async Task StopReleasesOwnedUnreadyReservationAfterDesktopCancellation()
    {
        var directory = await DirectoryAsync();
        var desktop = new RecordingDesktopLifecycle();
        using var lifecycle = Lifecycle(
            directory,
            desktop,
            new QueuePortAllocator(5_107));
        await lifecycle.ReserveAsync(Cwd, SessionId);

        await lifecycle.StopAsync(Context, SessionId, "cancel startup");

        Assert.AreEqual(0, directory.ListRegistrations().Count);
        Assert.AreEqual("stop", desktop.Calls.Single().Operation);
    }

    [TestMethod]
    public async Task PassiveInvalidAndDisposedCallsFailBeforeDependencies()
    {
        var directory = await DirectoryAsync();
        var desktop = new RecordingDesktopLifecycle();
        var allocator = new QueuePortAllocator(5_108);
        using var passive = new ActiveOpenCodeRuntimeLifecycle(
            BridgeHostOptions.Passive(Path.GetTempPath(), port: 0),
            directory,
            desktop,
            allocator);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.LaunchAsync(Context, SessionId, Cwd, elevated: false));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await passive.ReserveAsync(Cwd, SessionId));
        Assert.AreEqual(0, desktop.Calls.Count);
        Assert.AreEqual(0, allocator.Calls);

        var active = Lifecycle(directory, desktop, allocator);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            active.LaunchAsync(Context, "bad\nsession", Cwd, elevated: false));
        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await active.ReserveAsync("relative/path", SessionId));
        active.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
            active.ResumeAsync(Context, SessionId, Cwd));
    }

    [TestMethod]
    public async Task LoopbackAllocatorRejectsAnOccupiedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var allocator = new LoopbackOpenCodePortAllocator(port, port);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await allocator.AllocateAsync(new HashSet<int>()));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static ActiveOpenCodeRuntimeLifecycle Lifecycle(
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IManagedRuntimeLifecycle desktop,
        IBridgeOpenCodePortAllocator allocator,
        TimeSpan? readyTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
            ActiveOptions(),
            directory,
            desktop,
            allocator,
            readyTimeout,
            pollInterval: TimeSpan.FromMilliseconds(1),
            delay);

    private static async Task<ActiveOpenCodeEndpointDirectory> DirectoryAsync()
    {
        var directory = new ActiveOpenCodeEndpointDirectory(ActiveOptions());
        await directory.StartAsync(CancellationToken.None);
        return directory;
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.GetTempPath(),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "active-opencode-lifecycle-test");

    private sealed record DesktopCall(
        string Operation,
        string Runtime,
        string SessionId,
        string? Cwd,
        string? Prompt,
        bool Elevated,
        string? Reason);

    private sealed class RecordingDesktopLifecycle : IManagedRuntimeLifecycle
    {
        public List<DesktopCall> Calls { get; } = [];

        public Task LaunchAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string cwd,
            string? prompt,
            bool elevated,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(
                "launch",
                runtime,
                sessionExternalId,
                cwd,
                prompt,
                elevated,
                null));
            return Task.CompletedTask;
        }

        public Task ResumeAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string? cwd,
            string? prompt,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(
                "resume",
                runtime,
                sessionExternalId,
                cwd,
                prompt,
                false,
                null));
            return Task.CompletedTask;
        }

        public Task StopAsync(
            RuntimeCommandContext context,
            string runtime,
            string sessionExternalId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(
                "stop",
                runtime,
                sessionExternalId,
                null,
                null,
                false,
                reason));
            return Task.CompletedTask;
        }
    }

    private sealed class QueuePortAllocator(params int[] ports)
        : IBridgeOpenCodePortAllocator
    {
        private readonly ConcurrentQueue<int> ports = new(ports);
        private int concurrency;

        public int Calls { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public List<IReadOnlySet<int>> Exclusions { get; } = [];

        public async ValueTask<int> AllocateAsync(
            IReadOnlySet<int> excludedPorts,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var current = Interlocked.Increment(ref concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, current);
            try
            {
                Exclusions.Add(excludedPorts.ToHashSet());
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return ports.TryDequeue(out var port)
                    ? port
                    : throw new InvalidOperationException("no test port");
            }
            finally
            {
                Interlocked.Decrement(ref concurrency);
            }
        }
    }
}
