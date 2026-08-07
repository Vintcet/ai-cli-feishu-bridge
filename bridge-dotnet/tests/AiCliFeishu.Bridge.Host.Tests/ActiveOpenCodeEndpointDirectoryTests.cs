using System.Collections.Concurrent;
using System.Net;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveOpenCodeEndpointDirectoryTests
{
    private static readonly string Cwd = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "opencode-directory-project"));

    [TestMethod]
    public async Task LifecyclePublishesOnlyReadyLoopbackEndpointsAndBoundSessions()
    {
        var directory = Directory();
        Assert.IsFalse(directory.Snapshot.Initialized);
        Assert.AreEqual("starting", directory.ComponentHealth.Status);

        await directory.StartAsync(CancellationToken.None);
        var registration = directory.Register(5_100, Cwd + Path.DirectorySeparatorChar);

        Assert.AreEqual(5_100, registration.Port);
        Assert.AreEqual(Cwd, registration.Cwd);
        Assert.AreEqual(1L, registration.Generation);
        Assert.IsFalse(registration.Ready);
        Assert.AreEqual("127.0.0.1", registration.Endpoint.BaseUri.Host);
        Assert.AreEqual(5_100, registration.Endpoint.BaseUri.Port);
        Assert.AreEqual(string.Empty, registration.Endpoint.BaseUri.Query);
        Assert.AreEqual(string.Empty, registration.Endpoint.BaseUri.UserInfo);
        Assert.IsFalse(registration.Endpoint.BaseUri.AbsoluteUri.Contains(
            Cwd,
            StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, directory.ListReady().Count);
        Assert.IsTrue(directory.SetReady(5_100, registration.Generation, true));
        Assert.IsTrue(directory.RememberSession(
            5_100,
            registration.Generation,
            "session-alpha"));

        var endpoint = directory.FindBySession("session-alpha");
        Assert.IsNotNull(endpoint);
        Assert.IsTrue(endpoint.Ready);
        Assert.AreEqual(Cwd, endpoint.Directory);
        Assert.AreEqual(1, directory.ListReady().Count);
        var snapshot = directory.Snapshot;
        Assert.AreEqual(1, snapshot.Registrations);
        Assert.AreEqual(1, snapshot.Ready);
        Assert.AreEqual(1, snapshot.Sessions);
        Assert.AreEqual("healthy", directory.ComponentHealth.Status);
        Assert.IsFalse(directory.ComponentHealth.Detail!.Contains(Cwd, StringComparison.Ordinal));
        Assert.IsFalse(directory.ComponentHealth.Detail.Contains(
            "session-alpha",
            StringComparison.Ordinal));
        Assert.IsFalse(directory.ComponentHealth.Detail.Contains(
            "5100",
            StringComparison.Ordinal));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await directory.StopAsync(cancellation.Token);
        Assert.IsFalse(directory.Snapshot.Initialized);
        Assert.AreEqual(0, directory.Snapshot.Registrations);
        Assert.AreEqual(0, directory.Snapshot.Sessions);
    }

    [TestMethod]
    public async Task PortReplacementInvalidatesStaleGenerationAndOwnedSessions()
    {
        var directory = Directory();
        await directory.StartAsync(CancellationToken.None);
        var first = directory.Register(5_101, Cwd);
        Assert.IsTrue(directory.SetReady(5_101, first.Generation, true));
        Assert.IsTrue(directory.RememberSession(
            5_101,
            first.Generation,
            "session-old"));

        var replacement = directory.Register(
            5_101,
            Path.Combine(Cwd, "replacement"));

        Assert.IsTrue(replacement.Generation > first.Generation);
        Assert.IsNull(directory.FindBySession("session-old"));
        Assert.IsFalse(directory.SetReady(5_101, first.Generation, true));
        Assert.IsFalse(directory.RememberSession(
            5_101,
            first.Generation,
            "session-stale"));
        Assert.IsFalse(directory.Unregister(5_101, first.Generation));
        Assert.IsTrue(directory.SetReady(5_101, replacement.Generation, true));
        Assert.IsTrue(directory.RememberSession(
            5_101,
            replacement.Generation,
            "session-new"));
        Assert.IsNotNull(directory.FindBySession("session-new"));

        Assert.IsTrue(directory.Unregister(5_101, replacement.Generation));
        Assert.IsNull(directory.FindBySession("session-new"));
        Assert.AreEqual(0, directory.ListRegistrations().Count);
    }

    [TestMethod]
    public async Task SessionMappingMovesAtomicallyAndOnlyCurrentOwnerCanForgetIt()
    {
        var directory = Directory();
        await directory.StartAsync(CancellationToken.None);
        var first = directory.Register(5_102, Cwd);
        var second = directory.Register(5_103, Path.Combine(Cwd, "second"));
        directory.SetReady(first.Port, first.Generation, true);
        directory.SetReady(second.Port, second.Generation, true);
        var topologyRevision = directory.Snapshot.Revision;

        Assert.IsTrue(directory.RememberSession(
            first.Port,
            first.Generation,
            "session-moved"));
        Assert.AreEqual(
            first.Port,
            directory.FindBySession("session-moved")?.BaseUri.Port);
        Assert.IsTrue(directory.RememberSession(
            second.Port,
            second.Generation,
            "session-moved"));
        Assert.AreEqual(
            second.Port,
            directory.FindBySession("session-moved")?.BaseUri.Port);

        Assert.IsFalse(directory.ForgetSession(
            first.Port,
            first.Generation,
            "session-moved"));
        Assert.IsNotNull(directory.FindBySession("session-moved"));
        Assert.IsTrue(directory.ForgetSession(
            second.Port,
            second.Generation,
            "session-moved"));
        Assert.IsNull(directory.FindBySession("session-moved"));
        Assert.AreEqual(topologyRevision, directory.Snapshot.Revision);
    }

    [TestMethod]
    public async Task SessionMappingsUseBoundedLeastRecentlyObservedRetention()
    {
        var directory = Directory(sessionCapacity: 2);
        await directory.StartAsync(CancellationToken.None);
        var registration = directory.Register(5_104, Cwd);
        directory.SetReady(registration.Port, registration.Generation, true);
        directory.RememberSession(
            registration.Port,
            registration.Generation,
            "session-1");
        directory.RememberSession(
            registration.Port,
            registration.Generation,
            "session-2");
        directory.RememberSession(
            registration.Port,
            registration.Generation,
            "session-1");
        directory.RememberSession(
            registration.Port,
            registration.Generation,
            "session-3");

        Assert.IsNotNull(directory.FindBySession("session-1"));
        Assert.IsNull(directory.FindBySession("session-2"));
        Assert.IsNotNull(directory.FindBySession("session-3"));
        Assert.AreEqual(2, directory.Snapshot.Sessions);
    }

    [TestMethod]
    public async Task RevisionWaiterObservesChangesWithoutCancellingOtherWaiters()
    {
        var directory = Directory();
        await directory.StartAsync(CancellationToken.None);
        var revision = directory.Snapshot.Revision;
        using var cancelled = new CancellationTokenSource();
        var cancelledWaiter = directory.WaitForChangeAsync(
            revision,
            cancelled.Token).AsTask();
        var liveWaiter = directory.WaitForChangeAsync(revision).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => cancelledWaiter);

        directory.Register(5_105, Cwd);

        var changedRevision = await liveWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(changedRevision > revision);
        Assert.AreEqual(
            changedRevision,
            await directory.WaitForChangeAsync(revision));
    }

    [TestMethod]
    public async Task ConcurrentReregistrationHasOneCurrentGeneration()
    {
        var directory = Directory();
        await directory.StartAsync(CancellationToken.None);
        var identities = new ConcurrentBag<BridgeOpenCodeEndpointIdentity>();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            identities.Add(directory.Register(5_106, Cwd)))));

        Assert.AreEqual(64, identities.Select(item => item.Generation).Distinct().Count());
        var current = directory.ListRegistrations().Single();
        Assert.AreEqual(identities.Max(item => item.Generation), current.Generation);
        Assert.IsTrue(directory.SetReady(current.Port, current.Generation, true));
        Assert.IsTrue(identities
            .Where(item => item.Generation != current.Generation)
            .All(item => !directory.SetReady(item.Port, item.Generation, true)));
    }

    [TestMethod]
    public async Task PassiveModeInvalidIdentityAndCapacityFailClosed()
    {
        var passive = new ActiveOpenCodeEndpointDirectory(
            BridgeHostOptions.Passive(Path.GetTempPath(), port: 0));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            passive.StartAsync(CancellationToken.None));

        var directory = Directory(registrationCapacity: 1);
        await directory.StartAsync(CancellationToken.None);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            directory.Register(0, Cwd));
        Assert.ThrowsException<ArgumentException>(() =>
            directory.Register(5_107, "relative/path"));
        directory.Register(5_107, Cwd);
        Assert.ThrowsException<InvalidOperationException>(() =>
            directory.Register(5_108, Path.Combine(Cwd, "other")));
        Assert.ThrowsException<ArgumentException>(() =>
            directory.RememberSession(5_107, 1, "bad\nsession"));
        Assert.IsNull(directory.FindBySession(string.Empty));
    }

    private static ActiveOpenCodeEndpointDirectory Directory(
        int registrationCapacity = 16,
        int sessionCapacity = 32) => new(
            new BridgeHostOptions(
                Path.GetTempPath(),
                IPAddress.Loopback,
                0,
                BridgeOwnershipMode.Active,
                "opencode-directory-test"),
            registrationCapacity,
            sessionCapacity);
}
