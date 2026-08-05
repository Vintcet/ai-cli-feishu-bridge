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
    public void ActiveOwnershipIsRejectedUntilCutoverIsImplemented()
    {
        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostOptions.Parse(
                ["--ownership", "active"],
                Path.GetTempPath()));

        StringAssert.Contains(error.Message, "Active Owner");
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
