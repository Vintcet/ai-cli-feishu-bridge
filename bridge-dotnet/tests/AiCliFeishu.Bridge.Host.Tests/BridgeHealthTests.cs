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
}
