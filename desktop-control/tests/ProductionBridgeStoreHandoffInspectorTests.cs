using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishuControl;

[TestClass]
public sealed class ProductionBridgeStoreHandoffInspectorTests
{
    [TestMethod]
    public async Task StableMissingLeaseAndCompatibleStorePermitHandoff()
    {
        var leases = LeaseSequence(Missing(), Missing());
        var storeChecks = 0;
        var inspector = Inspector(leases, () => storeChecks++);

        var evidence = await inspector.InspectAsync(default);

        Assert.IsTrue(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Missing, evidence.LeaseState);
        Assert.AreEqual(1, storeChecks);
        Assert.AreEqual(2, leases.Calls);
    }

    [TestMethod]
    public async Task StableStaleLeaseCannotProveTheStoreWasFlushed()
    {
        var stale = Stale("node-lease-a");
        var inspector = Inspector(LeaseSequence(stale, stale));

        var evidence = await inspector.InspectAsync(default);

        Assert.IsFalse(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Stale, evidence.LeaseState);
    }

    [DataTestMethod]
    [DataRow("Live", "Live")]
    [DataRow("Invalid", "Invalid")]
    public async Task UnsafeLeaseStateRejectsBeforeReadingTheStore(
        string activeOwnerState,
        string cutoverState)
    {
        var state = Enum.Parse<ActiveOwnerLeaseState>(activeOwnerState);
        var snapshot = state is ActiveOwnerLeaseState.Live
            ? Live("node-live")
            : new ActiveOwnerLeaseSnapshot(state);
        var leases = LeaseSequence(snapshot);
        var storeChecks = 0;
        var inspector = Inspector(leases, () => storeChecks++);

        var evidence = await inspector.InspectAsync(default);

        Assert.IsFalse(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(
            Enum.Parse<BridgeCutoverLeaseState>(cutoverState),
            evidence.LeaseState);
        Assert.AreEqual(0, storeChecks);
        Assert.AreEqual(1, leases.Calls);
    }

    [TestMethod]
    public async Task ANewOwnerAppearingDuringStoreValidationIsInvalid()
    {
        var inspector = Inspector(LeaseSequence(Missing(), Live("node-new")));

        var evidence = await inspector.InspectAsync(default);

        Assert.IsFalse(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Invalid, evidence.LeaseState);
    }

    [TestMethod]
    public async Task ALeaseDisappearingDuringStoreValidationIsInvalid()
    {
        var inspector = Inspector(LeaseSequence(
            Stale("node-stale"),
            Missing()));

        var evidence = await inspector.InspectAsync(default);

        Assert.IsFalse(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Invalid, evidence.LeaseState);
    }

    [TestMethod]
    public async Task AnInvalidLeaseAppearingDuringStoreValidationIsInvalid()
    {
        var inspector = Inspector(LeaseSequence(
            Missing(),
            new ActiveOwnerLeaseSnapshot(ActiveOwnerLeaseState.Invalid)));

        var evidence = await inspector.InspectAsync(default);

        Assert.IsFalse(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Invalid, evidence.LeaseState);
    }

    [TestMethod]
    public async Task AChangedStaleLeaseIdentityIsInvalid()
    {
        var inspector = Inspector(LeaseSequence(
            Stale("node-stale-a"),
            Stale("node-stale-b")));

        var evidence = await inspector.InspectAsync(default);

        Assert.IsFalse(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Invalid, evidence.LeaseState);
    }

    [TestMethod]
    public async Task IncompatibleStoreIsReportedWithoutSkippingTheSecondLeaseRead()
    {
        var leases = LeaseSequence(Missing(), Missing());
        var inspector = new ProductionBridgeStoreHandoffInspector(
            leases.InspectAsync,
            _ => throw new NodeStoreValidationException(
                "sessions.json",
                ["sessions 必须是对象"]));

        var evidence = await inspector.InspectAsync(default);

        Assert.IsTrue(evidence.StoreFlushed);
        Assert.IsFalse(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Missing, evidence.LeaseState);
        Assert.AreEqual(2, leases.Calls);
    }

    [TestMethod]
    public async Task MissingStoreFilesUseTheCompatibleNodeFallbacks()
    {
        await using var directory = new TemporaryDirectory();
        var inspector = new ProductionBridgeStoreHandoffInspector(directory.Path);

        var evidence = await inspector.InspectAsync(default);

        Assert.IsTrue(evidence.StoreFlushed);
        Assert.IsTrue(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Missing, evidence.LeaseState);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(directory.Path).Count());
    }

    [TestMethod]
    public async Task RealInspectorNeverRepairsOrRewritesAnInvalidStore()
    {
        await using var directory = new TemporaryDirectory();
        var sessions = Path.Combine(directory.Path, "sessions.json");
        const string invalid = "{\"sessions\":{\"bad\":{}}}";
        await File.WriteAllTextAsync(sessions, invalid);
        var inspector = new ProductionBridgeStoreHandoffInspector(directory.Path);

        var evidence = await inspector.InspectAsync(default);

        Assert.IsTrue(evidence.StoreFlushed);
        Assert.IsFalse(evidence.StoreCompatible);
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(sessions));
        CollectionAssert.AreEquivalent(
            new[] { "sessions.json" },
            Directory.EnumerateFiles(directory.Path)
                .Select(Path.GetFileName)
                .ToArray());
        Assert.AreEqual(0, Directory.EnumerateDirectories(directory.Path).Count());
    }

    [TestMethod]
    public async Task MalformedJsonIsIncompatibleAndRemainsUnchanged()
    {
        await using var directory = new TemporaryDirectory();
        var sessions = Path.Combine(directory.Path, "sessions.json");
        const string malformed = "{\"sessions\":";
        await File.WriteAllTextAsync(sessions, malformed);
        var inspector = new ProductionBridgeStoreHandoffInspector(directory.Path);

        var evidence = await inspector.InspectAsync(default);

        Assert.IsTrue(evidence.StoreFlushed);
        Assert.IsFalse(evidence.StoreCompatible);
        Assert.AreEqual(BridgeCutoverLeaseState.Missing, evidence.LeaseState);
        Assert.AreEqual(malformed, await File.ReadAllTextAsync(sessions));
        CollectionAssert.AreEquivalent(
            new[] { "sessions.json" },
            Directory.EnumerateFiles(directory.Path)
                .Select(Path.GetFileName)
                .ToArray());
    }

    [TestMethod]
    public async Task CancellationIsNotConvertedIntoCompatibilityEvidence()
    {
        var inspector = new ProductionBridgeStoreHandoffInspector(
            LeaseSequence(Missing()).InspectAsync,
            _ => Task.FromCanceled(new CancellationToken(canceled: true)));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
            await inspector.InspectAsync(default));
    }

    [TestMethod]
    public async Task UnexpectedStoreFailureIsNotConvertedIntoCompatibilityEvidence()
    {
        var inspector = new ProductionBridgeStoreHandoffInspector(
            LeaseSequence(Missing()).InspectAsync,
            _ => throw new InvalidOperationException("test failure"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await inspector.InspectAsync(default));
    }

    private static ProductionBridgeStoreHandoffInspector Inspector(
        LeaseReader leases,
        Action? validate = null) =>
        new(
            leases.InspectAsync,
            _ =>
            {
                validate?.Invoke();
                return Task.CompletedTask;
            });

    private static LeaseReader LeaseSequence(
        params ActiveOwnerLeaseSnapshot[] snapshots) => new(snapshots);

    private static ActiveOwnerLeaseSnapshot Missing() =>
        new(ActiveOwnerLeaseState.Missing);

    private static ActiveOwnerLeaseSnapshot Live(string leaseId) =>
        new(ActiveOwnerLeaseState.Live, Record(leaseId));

    private static ActiveOwnerLeaseSnapshot Stale(string leaseId) =>
        new(ActiveOwnerLeaseState.Stale, Record(leaseId));

    private static ActiveOwnerLeaseRecord Record(string leaseId) => new(
        ActiveOwnerLeaseObserver.SchemaVersion,
        "node",
        "active",
        71001,
        "production",
        leaseId,
        DateTimeOffset.Parse("2026-08-07T00:00:00.000Z"));

    private sealed class LeaseReader(IEnumerable<ActiveOwnerLeaseSnapshot> snapshots)
    {
        private readonly Queue<ActiveOwnerLeaseSnapshot> remaining = new(snapshots);

        public int Calls { get; private set; }

        public ValueTask<ActiveOwnerLeaseSnapshot> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(remaining.Dequeue());
        }
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ai-cli-feishu-handoff-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
