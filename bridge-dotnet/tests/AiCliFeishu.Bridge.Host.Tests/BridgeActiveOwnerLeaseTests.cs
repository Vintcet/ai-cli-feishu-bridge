using System.Text.Json;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeActiveOwnerLeaseTests
{
    private string? directory;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            $"ai-cli-feishu-owner-observer-{Guid.NewGuid():N}");
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
    public async Task ReadsTheNodeLeaseContractWithoutClaimingIt()
    {
        var observer = Observer(processId => processId == 51001);
        var record = Record(processId: 51001);
        await WriteRecordAsync(observer, record);

        var snapshot = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Live, snapshot.State);
        Assert.AreEqual(record, snapshot.Record);
        Assert.IsTrue(File.Exists(observer.MetadataPath));
    }

    [TestMethod]
    public async Task ReadsTheSharedNodeLeaseExample()
    {
        var observer = Observer(processId => processId == 3210);
        Directory.CreateDirectory(Path.GetDirectoryName(observer.MetadataPath)!);
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "OwnershipExamples",
                "active-owner-node.json"),
            observer.MetadataPath);

        var snapshot = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Live, snapshot.State);
        Assert.AreEqual("node", snapshot.Record?.HostKind);
        Assert.AreEqual("production", snapshot.Record?.InstanceName);
        Assert.AreEqual(3210, snapshot.Record?.ProcessId);
    }

    [TestMethod]
    public async Task DistinguishesAStaleOwnerFromAMissingLease()
    {
        var observer = Observer(_ => false);
        await WriteRecordAsync(observer, Record(processId: 52001));

        var stale = await observer.InspectAsync();
        Directory.Delete(Path.GetDirectoryName(observer.MetadataPath)!, recursive: true);
        var missing = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Stale, stale.State);
        Assert.AreEqual(52001, stale.Record?.ProcessId);
        Assert.AreEqual(ActiveOwnerLeaseState.Missing, missing.State);
    }

    [TestMethod]
    public async Task InvalidOrIncompleteLeaseMetadataIsNeverTreatedAsMissing()
    {
        var observer = Observer(_ => false);
        Directory.CreateDirectory(Path.GetDirectoryName(observer.MetadataPath)!);

        var incomplete = await observer.InspectAsync();
        await File.WriteAllTextAsync(observer.MetadataPath, "{}\n");
        var invalid = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Invalid, incomplete.State);
        Assert.AreEqual(ActiveOwnerLeaseState.Invalid, invalid.State);
    }

    [TestMethod]
    public async Task UnsafeLeaseIdentityIsInvalid()
    {
        var observer = Observer(_ => false);
        await WriteRecordAsync(
            observer,
            Record(processId: 52002) with { LeaseId = "../outside" });

        var snapshot = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Invalid, snapshot.State);
    }

    [TestMethod]
    public async Task ContractRejectsUnicodeCaseChangesAndUnknownFields()
    {
        var observer = Observer(_ => false);
        Directory.CreateDirectory(Path.GetDirectoryName(observer.MetadataPath)!);

        await File.WriteAllTextAsync(
            observer.MetadataPath,
            JsonSerializer.Serialize(
                Record(processId: 52003) with { InstanceName = "生产" },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
        Assert.AreEqual(
            ActiveOwnerLeaseState.Invalid,
            (await observer.InspectAsync()).State);

        await File.WriteAllTextAsync(
            observer.MetadataPath,
            """
            {"SchemaVersion":1,"hostKind":"node","ownershipMode":"active","processId":52003,"instanceName":"production","leaseId":"lease-1","acquiredAt":"2026-08-06T10:00:00.000Z"}
            """);
        Assert.AreEqual(
            ActiveOwnerLeaseState.Invalid,
            (await observer.InspectAsync()).State);

        await File.WriteAllTextAsync(
            observer.MetadataPath,
            """
            {"schemaVersion":1,"hostKind":"node","ownershipMode":"active","processId":52003,"instanceName":"production","leaseId":"lease-1","acquiredAt":"2026-08-06T10:00:00.000Z","futureField":true}
            """);
        Assert.AreEqual(
            ActiveOwnerLeaseState.Invalid,
            (await observer.InspectAsync()).State);
    }

    [TestMethod]
    public async Task AFileAtTheLockPathIsInvalidRatherThanMissing()
    {
        var observer = Observer(_ => false);
        Directory.CreateDirectory(directory!);
        await File.WriteAllTextAsync(observer.LockDirectoryPath, "not-a-directory");

        var snapshot = await observer.InspectAsync();

        Assert.AreEqual(ActiveOwnerLeaseState.Invalid, snapshot.State);
    }

    [TestMethod]
    public async Task PassiveGuardReportsLeaseStateWithoutBecomingActiveOwner()
    {
        var observer = Observer(processId => processId == 53001);
        await WriteRecordAsync(observer, Record(processId: 53001));
        var guard = new PassiveOwnerGuardSubsystem(observer);

        await guard.StartAsync(CancellationToken.None);

        Assert.AreEqual("passive", guard.ComponentHealth.Status);
        Assert.AreEqual("active-owner-node-live", guard.ComponentHealth.Detail);
        Assert.IsTrue(File.Exists(observer.MetadataPath));

        await guard.StopAsync(CancellationToken.None);
        Assert.AreEqual("starting", guard.ComponentHealth.Status);
    }

    private ActiveOwnerLeaseObserver Observer(Func<int, bool> processAlive) =>
        new(directory!, processAlive);

    private static ActiveOwnerLeaseRecord Record(int processId) => new(
        ActiveOwnerLeaseObserver.SchemaVersion,
        "node",
        "active",
        processId,
        "production",
        "node-lease-1",
        DateTimeOffset.Parse("2026-08-06T10:00:00.000Z"));

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
}
