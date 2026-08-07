using System.Net;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeProductionStoreOwnerTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize() => directory = Path.Combine(
        Path.GetTempPath(),
        $"bridge-active-store-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpensExistingStoreOnlyWhileMatchingLeaseIsHeld()
    {
        Directory.CreateDirectory(directory!);
        var sessions = Path.Combine(directory!, "sessions.json");
        const string source = """
            {"sessions":{"secret-session":{"sessionId":"secret-session","cwd":"K:\\secret","status":"waiting","runtime":"codex","lastSeenAt":"2026-08-06T00:00:00Z"}}}
            """;
        await File.WriteAllTextAsync(sessions, source);
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease);

        await owner.OpenAsync();

        Assert.AreEqual(BridgeProductionStoreState.Open, owner.Snapshot.State);
        Assert.AreEqual(1, owner.Snapshot.StoreFiles);
        Assert.IsNull(owner.Snapshot.Store);
        Assert.AreEqual(1, owner.CurrentSnapshot.Store!.Sessions.Sessions.Count);
        Assert.AreEqual(
            new BridgeComponentHealth("production-store", "ready", "loaded files=1"),
            owner.ComponentHealth);
        Assert.AreEqual(source, await File.ReadAllTextAsync(sessions));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "sessions.json",
                ActiveOwnerLeaseObserver.MetadataFileName,
            },
            Directory.EnumerateFiles(directory!, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray());
    }

    [TestMethod]
    public async Task RejectsOpeningBeforeLeaseWithoutAccessingStore()
    {
        var lease = Lease();
        var owner = Owner(lease);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await owner.OpenAsync());

        StringAssert.Contains(error.Message, "租约持有期间");
        Assert.AreEqual(BridgeProductionStoreState.NotOpened, owner.Snapshot.State);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task RejectsReplacedLeaseBeforeWritingProductionStore()
    {
        Directory.CreateDirectory(directory!);
        var settings = Path.Combine(directory!, "settings.json");
        const string source = "{\"notifyActivity\":false}";
        await File.WriteAllTextAsync(settings, source);
        await using var lease = Lease();
        await lease.AcquireAsync();
        var observed = new ActiveOwnerLeaseSnapshot(
            ActiveOwnerLeaseState.Live,
            lease.Record with { LeaseId = "replacement-owner" });
        var owner = Owner(lease, _ => ValueTask.FromResult(observed));

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await owner.OpenAsync());

        StringAssert.Contains(error.Message, "身份已变化");
        Assert.AreEqual(source, await File.ReadAllTextAsync(settings));
        CollectionAssert.AreEquivalent(
            new[] { "settings.json", ActiveOwnerLeaseObserver.MetadataFileName },
            Directory.EnumerateFiles(directory!, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray());
    }

    [TestMethod]
    public async Task CloseIgnoresCancelledShutdownAndFlushesBeforeLeaseRelease()
    {
        Directory.CreateDirectory(directory!);
        var sessions = Path.Combine(directory!, "sessions.json");
        await File.WriteAllTextAsync(sessions, "{\"sessions\":{}}");
        await using var lease = Lease();
        await lease.AcquireAsync();
        var owner = Owner(lease);
        await owner.OpenAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await owner.CloseAsync(cancelled.Token);

        Assert.AreEqual(BridgeProductionStoreState.Closed, owner.Snapshot.State);
        Assert.IsNull(owner.Snapshot.Store);
        Assert.IsTrue(lease.IsHeld);
        Assert.IsTrue(Directory.Exists(lease.LockDirectoryPath));
        CollectionAssert.AreEquivalent(
            NodeStoreFile.All.Select(file => file.FileName).ToArray(),
            Directory.EnumerateFiles(directory!, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray());
        Assert.IsFalse(Directory.EnumerateFiles(directory!, "*.tmp").Any());

        await lease.ReleaseAsync();
        Assert.IsFalse(Directory.Exists(lease.LockDirectoryPath));
    }

    [TestMethod]
    public void RejectsPassiveOptionsAndCopyRepositoryBeforeFileAccess()
    {
        var passive = BridgeHostOptions.Passive(directory!, port: 0);
        var lease = new RecordingLease();

        var passiveError = Assert.ThrowsException<InvalidOperationException>(() =>
            _ = new ActiveProductionStoreOwner(
                passive,
                lease,
                new NodeJsonStoreRepository(directory!, NodeStoreAccess.ReadWriteActiveOwner)));
        StringAssert.Contains(passiveError.Message, "只能用于 Active Host");

        var copyError = Assert.ThrowsException<InvalidOperationException>(() =>
            _ = new ActiveProductionStoreOwner(
                Options(),
                lease,
                new NodeJsonStoreRepository(directory!, NodeStoreAccess.ReadWriteCopy)));
        StringAssert.Contains(copyError.Message, "Active Owner 专用写入");
        Assert.IsFalse(Directory.Exists(directory));
    }

    private BridgeHostOptions Options() => new(
        directory!,
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "active-store-test");

    private ActiveOwnerLeaseAcquirer Lease() => new(Options());

    private ActiveProductionStoreOwner Owner(
        ActiveOwnerLeaseAcquirer lease,
        Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>>? inspect = null) => new(
            Options(),
            lease,
            new NodeJsonStoreRepository(
                directory!,
                NodeStoreAccess.ReadWriteActiveOwner),
            inspect);

    private sealed class RecordingLease : IBridgeActiveOwnerLeaseLifecycle
    {
        public bool IsHeld => false;

        public ActiveOwnerLeaseRecord? HeldLease => null;

        public ValueTask<ActiveOwnerLeaseRecord> AcquireAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
