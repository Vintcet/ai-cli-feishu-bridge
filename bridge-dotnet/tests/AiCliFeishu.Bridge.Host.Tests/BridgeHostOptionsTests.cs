using System.Net;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeHostOptionsTests
{
    [TestMethod]
    public void DefaultsToPassiveLoopbackHost()
    {
        var options = BridgeHostOptions.Parse([], Path.GetTempPath());

        Assert.AreEqual(BridgeOwnershipMode.Passive, options.OwnershipMode);
        Assert.AreEqual(IPAddress.Loopback, options.ListenAddress);
        Assert.AreEqual(BridgeHostOptions.DefaultPassivePort, options.Port);
    }

    [TestMethod]
    public void ActiveOwnershipUsesTheSingleOwnerLeaseWithoutExtraArguments()
    {
        var options = BridgeHostOptions.Parse(
            [
                "--ownership", "active",
                "--instance", "production-dotnet",
            ],
            Path.GetTempPath());

        Assert.AreEqual(BridgeOwnershipMode.Active, options.OwnershipMode);
        Assert.AreEqual("production-dotnet", options.InstanceName);
    }

    [TestMethod]
    public void NonLoopbackControlEndpointIsRejected()
    {
        var options = new BridgeHostOptions(
            Path.GetTempPath(),
            IPAddress.Any,
            8765,
            BridgeOwnershipMode.Passive,
            "test");

        Assert.ThrowsException<InvalidOperationException>(options.Validate);
    }
}
