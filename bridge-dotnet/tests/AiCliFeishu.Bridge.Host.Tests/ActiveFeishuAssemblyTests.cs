using System.Net;
using AiCliFeishu.Bridge.Adapters.Feishu;
using Microsoft.Extensions.DependencyInjection;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuAssemblyTests
{
    [TestMethod]
    public async Task ActiveGraphResolvesStandardFeishuBoundaryWithoutExternalIo()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"active-feishu-assembly-{Guid.NewGuid():N}");
        var options = new BridgeHostOptions(
            dataDirectory,
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "active-feishu-assembly-test");
        var services = new ServiceCollection();
        services.AddSingleton(options);
        BridgeHostApplication.AddOwnershipAssembly(services, options);

        var preflight = BridgeProductionAssemblyPreflight.Validate(options, services);
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetServices<IBridgeFeishuIntentHandler>().Single();
        var ingress = provider.GetRequiredService<IFeishuIntentSink>();
        var adapter = provider.GetRequiredService<IBridgeFeishuAdapterAssembly>();
        var boundaries = provider.GetRequiredService<BridgeBoundaryCatalog>();
        var eventSubsystem = provider.GetRequiredService<BridgeFeishuEventSubsystem>();

        var adapterSnapshot = adapter.Validate();
        var boundarySnapshot = boundaries.Validate();

        Assert.AreEqual("active", preflight.Mode);
        Assert.IsTrue(preflight.Complete);
        Assert.IsInstanceOfType<ActiveFeishuIntentHandler>(handler);
        Assert.IsInstanceOfType<BridgeFeishuIntentIngress>(ingress);
        Assert.AreEqual("active", adapterSnapshot.Mode);
        Assert.IsTrue(adapterSnapshot.LiveEventStreamEnabled);
        Assert.IsTrue(adapterSnapshot.OutboundMessagingEnabled);
        Assert.AreEqual(1, boundarySnapshot.FeishuIntentHandlers);
        Assert.IsFalse(boundarySnapshot.Passive);
        Assert.IsNull(eventSubsystem.Completion);
        Assert.IsFalse(Directory.Exists(dataDirectory));
    }
}
