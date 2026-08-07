using System.Text.Json;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeActiveOwnerLeaseAcquirerTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-owner-acquirer-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishesAndReleasesTheSharedDotnetLeaseContract()
    {
        var lease = Acquirer(
            processId: 61001,
            liveProcesses: [61001],
            leaseId: "dotnet-lease-1");

        var record = await lease.AcquireAsync();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(lease.MetadataPath));

        Assert.IsTrue(lease.IsHeld);
        Assert.AreEqual("dotnet", record.HostKind);
        Assert.AreEqual("active", record.OwnershipMode);
        Assert.AreEqual(61001, record.ProcessId);
        Assert.AreEqual("cutover-test", record.InstanceName);
        Assert.AreEqual("dotnet-lease-1", record.LeaseId);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "hostKind",
                "ownershipMode",
                "processId",
                "instanceName",
                "leaseId",
                "acquiredAt",
            },
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("dotnet", json.RootElement.GetProperty("hostKind").GetString());
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(directory!, "*pending*").Any());

        await lease.ReleaseAsync();

        Assert.IsFalse(lease.IsHeld);
        Assert.IsFalse(Directory.Exists(lease.LockDirectoryPath));
    }

    [TestMethod]
    public async Task ObserverReadsTheSharedDotnetLeaseExample()
    {
        var observer = Observer(processId => processId == 61001);
        Directory.CreateDirectory(Path.GetDirectoryName(observer.MetadataPath)!);
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "OwnershipExamples",
                "active-owner-dotnet.json"),
            observer.MetadataPath);

        var snapshot = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Live, snapshot.State);
        Assert.AreEqual("dotnet", snapshot.Record?.HostKind);
        Assert.AreEqual("cutover-test", snapshot.Record?.InstanceName);
        Assert.AreEqual("dotnet-lease-1", snapshot.Record?.LeaseId);
    }

    [TestMethod]
    public async Task RejectsASecondLiveOwner()
    {
        await using var first = Acquirer(
            processId: 61011,
            liveProcesses: [61011],
            leaseId: "first-live");
        var second = Acquirer(
            processId: 61012,
            liveProcesses: [61011, 61012],
            leaseId: "second-live");
        await first.AcquireAsync();

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await second.AcquireAsync());

        StringAssert.Contains(error.Message, "61011");
        Assert.IsTrue(first.IsHeld);
        Assert.IsFalse(second.IsHeld);
    }

    [TestMethod]
    public async Task ReclaimsADeadNodeLeaseIntoADeterministicTombstone()
    {
        var observer = Observer(processId => processId == 61022);
        var stale = Record(
            hostKind: "node",
            processId: 61021,
            leaseId: "dead-node");
        await WriteRecordAsync(observer, stale);
        await using var replacement = Acquirer(
            processId: 61022,
            liveProcesses: [61022],
            leaseId: "dotnet-replacement");

        var acquired = await replacement.AcquireAsync();
        var tombstone = Path.Combine(
            directory!,
            "bridge-active-owner.stale-dead-node",
            ActiveOwnerLeaseObserver.MetadataFileName);

        Assert.AreEqual("dotnet-replacement", acquired.LeaseId);
        Assert.IsTrue(File.Exists(tombstone));
        var preserved = JsonSerializer.Deserialize<ActiveOwnerLeaseRecord>(
            await File.ReadAllTextAsync(tombstone),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.AreEqual(stale, preserved);
    }

    [TestMethod]
    public async Task ConcurrentReclaimersLeaveExactlyOneLiveOwner()
    {
        var observer = Observer(processId => processId is 61032 or 61033);
        await WriteRecordAsync(
            observer,
            Record(hostKind: "node", processId: 61031, leaseId: "dead-race"));
        var left = Acquirer(
            processId: 61032,
            liveProcesses: [61032, 61033],
            leaseId: "race-left");
        var right = Acquirer(
            processId: 61033,
            liveProcesses: [61032, 61033],
            leaseId: "race-right");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[]
        {
            AttemptAsync(left, gate.Task),
            AttemptAsync(right, gate.Task),
        };
        gate.SetResult();
        var errors = await Task.WhenAll(attempts);

        Assert.AreEqual(1, errors.Count(error => error is null));
        Assert.AreEqual(1, errors.Count(error => error is InvalidOperationException));
        Assert.AreEqual(1, new[] { left, right }.Count(lease => lease.IsHeld));
        Assert.IsTrue(Directory.Exists(Path.Combine(
            directory!,
            "bridge-active-owner.stale-dead-race")));
        Assert.IsTrue((await observer.InspectAsync()).State is ActiveOwnerLeaseState.Live);

        var winner = left.IsHeld ? left : right;
        await winner.ReleaseAsync();
    }

    [TestMethod]
    public async Task InvalidMetadataIsNeverReplaced()
    {
        var lease = Acquirer(
            processId: 61041,
            liveProcesses: [],
            leaseId: "invalid-test");
        Directory.CreateDirectory(lease.LockDirectoryPath);
        await File.WriteAllTextAsync(lease.MetadataPath, "{}\n");

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await lease.AcquireAsync());

        StringAssert.Contains(error.Message, "无效");
        Assert.AreEqual("{}\n", await File.ReadAllTextAsync(lease.MetadataPath));
        Assert.IsFalse(lease.IsHeld);
    }

    [TestMethod]
    public async Task ReleaseNeverDeletesAReplacementOwner()
    {
        var lease = Acquirer(
            processId: 61051,
            liveProcesses: [61051, 61052],
            leaseId: "original-dotnet");
        await lease.AcquireAsync();
        var replacement = Record(
            hostKind: "node",
            processId: 61052,
            leaseId: "replacement-node");
        await WriteRecordAsync(Observer(_ => true), replacement);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await lease.ReleaseAsync());

        StringAssert.Contains(error.Message, "身份已变化");
        Assert.IsTrue(File.Exists(lease.MetadataPath));
        var snapshot = await Observer(_ => true).InspectAsync();
        Assert.AreEqual("replacement-node", snapshot.Record?.LeaseId);
    }

    [TestMethod]
    public async Task HostedLifecycleAcquiresAndReleasesTheProductionLease()
    {
        var options = new BridgeHostOptions(
            directory!,
            System.Net.IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "active-lifecycle");
        await using var lease = new ActiveOwnerLeaseAcquirer(options);
        var health = new BridgeHealthRegistry(options);
        var service = new ActiveOwnerLeaseHostedService(lease, health);

        await service.StartAsync(CancellationToken.None);

        Assert.IsTrue(lease.IsHeld);
        Assert.AreEqual(Environment.ProcessId, lease.Record.ProcessId);
        Assert.AreEqual("active-lifecycle", lease.Record.InstanceName);
        Assert.AreEqual(
            new BridgeComponentHealth(
                "production-owner",
                "ready",
                "active-owner-dotnet-held"),
            health.Snapshot().Components.Single(component =>
                component.Name == "production-owner"));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await service.StopAsync(cancelled.Token);

        Assert.IsFalse(lease.IsHeld);
        Assert.IsFalse(Directory.Exists(lease.LockDirectoryPath));
        Assert.AreEqual(
            "stopped",
            health.Snapshot().Components.Single(component =>
                component.Name == "production-owner").Status);
    }

    [TestMethod]
    public async Task HostedLifecyclePreservesALiveOwnerAndReportsFailure()
    {
        var options = new BridgeHostOptions(
            directory!,
            System.Net.IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "active-lifecycle");
        var observer = Observer(processId => processId == Environment.ProcessId);
        var existing = Record(
            hostKind: "node",
            processId: Environment.ProcessId,
            leaseId: "live-node-owner");
        await WriteRecordAsync(observer, existing);
        await using var lease = new ActiveOwnerLeaseAcquirer(options);
        var health = new BridgeHealthRegistry(options);
        var service = new ActiveOwnerLeaseHostedService(lease, health);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await service.StartAsync(CancellationToken.None));

        StringAssert.Contains(error.Message, Environment.ProcessId.ToString());
        Assert.IsFalse(lease.IsHeld);
        Assert.AreEqual(
            "failed",
            health.Snapshot().Components.Single(component =>
                component.Name == "production-owner").Status);
        var preserved = await observer.InspectAsync();
        Assert.AreEqual(ActiveOwnerLeaseState.Live, preserved.State);
        Assert.AreEqual(existing, preserved.Record);
    }

    [TestMethod]
    public void ProductionLifecycleRejectsPassiveOptionsBeforeFileAccess()
    {
        var options = BridgeHostOptions.Passive(directory!, port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            _ = new ActiveOwnerLeaseAcquirer(options));

        StringAssert.Contains(error.Message, "只能用于 Active Host");
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void PassiveProductionHostDoesNotRegisterTheAcquirer()
    {
        using var app = BridgeHostApplication.Build(
            BridgeHostOptions.Passive(directory!, port: 0));

        Assert.IsNull(app.Services.GetService(typeof(ActiveOwnerLeaseAcquirer)));
    }

    private ActiveOwnerLeaseAcquirer Acquirer(
        int processId,
        int[] liveProcesses,
        string leaseId) => new(
            directory!,
            "cutover-test",
            processId,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-06T10:00:00.000Z")),
            candidate => liveProcesses.Contains(candidate),
            () => leaseId);

    private ActiveOwnerLeaseObserver Observer(Func<int, bool> processAlive) =>
        new(directory!, processAlive);

    private static ActiveOwnerLeaseRecord Record(
        string hostKind,
        int processId,
        string leaseId) => new(
            ActiveOwnerLeaseObserver.SchemaVersion,
            hostKind,
            "active",
            processId,
            "production",
            leaseId,
            DateTimeOffset.Parse("2026-08-06T09:00:00.000Z"));

    private static async Task WriteRecordAsync(
        ActiveOwnerLeaseObserver observer,
        ActiveOwnerLeaseRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(observer.MetadataPath)!);
        await File.WriteAllTextAsync(
            observer.MetadataPath,
            JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }) + "\n");
    }

    private static async Task<Exception?> AttemptAsync(
        ActiveOwnerLeaseAcquirer lease,
        Task gate)
    {
        await gate;
        try
        {
            await lease.AcquireAsync();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
