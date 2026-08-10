namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeHealthTests
{
    [TestMethod]
    public void PassiveOwnerCanBeHealthyWithoutClaimingProductionOwnership()
    {
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        health.Report("production-owner", "passive");
        health.SetLifecycle(BridgeHostLifecycleState.Ready);

        var snapshot = health.Snapshot();

        Assert.IsTrue(snapshot.Ok);
        Assert.IsFalse(snapshot.ActiveOwner);
        Assert.AreEqual("passive", snapshot.OwnershipMode);
        Assert.AreEqual("ready", snapshot.Status);
    }

    [TestMethod]
    public void StartingOrFailedComponentIsNotHealthy()
    {
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        health.Report("adapter", "failed", "synthetic failure");
        health.SetLifecycle(BridgeHostLifecycleState.Ready);

        Assert.IsFalse(health.Snapshot().Ok);
    }

    [TestMethod]
    public void HealthyBackgroundComponentKeepsReadyHostHealthy()
    {
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        health.Report("background-directory", "healthy");
        health.SetLifecycle(BridgeHostLifecycleState.Ready);

        Assert.IsTrue(health.Snapshot().Ok);
    }

    [TestMethod]
    public void TrackedComponentHealthIsReadLive()
    {
        var health = new BridgeHealthRegistry(BridgeHostOptions.Passive(Path.GetTempPath()));
        var component = new MutableComponentHealth("dynamic", "ready", "count=0");
        health.Track(component);
        health.SetLifecycle(BridgeHostLifecycleState.Ready);

        Assert.AreEqual("count=0", health.Snapshot().Components.Single().Detail);

        component.Detail = "count=1";
        Assert.AreEqual("count=1", health.Snapshot().Components.Single().Detail);

        component.Status = "failed";
        Assert.IsFalse(health.Snapshot().Ok);
    }

    private sealed class MutableComponentHealth(
        string name,
        string status,
        string? detail) : IBridgeHostSubsystemHealth
    {
        public string Status { get; set; } = status;

        public string? Detail { get; set; } = detail;

        public BridgeComponentHealth ComponentHealth => new(name, Status, Detail);
    }
}
