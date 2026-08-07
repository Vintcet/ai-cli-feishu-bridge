using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveRuntimeCommandAssemblyTests
{
    [TestMethod]
    public async Task ActiveGraphResolvesAndQueuesRuntimeLaunchesWithoutExternalIo()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"active-runtime-assembly-{Guid.NewGuid():N}");
        var cwd = Path.Combine(dataDirectory, "project");
        var options = new BridgeHostOptions(
            dataDirectory,
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "active-runtime-assembly-test");
        var services = new ServiceCollection();
        services.AddSingleton(options);
        BridgeHostApplication.AddOwnershipAssembly(services, options);

        var preflight = BridgeProductionAssemblyPreflight.Validate(options, services);
        await using var provider = services.BuildServiceProvider();
        var adapters = provider.GetServices<IRuntimeAdapter>().ToArray();
        var ingress = provider.GetRequiredService<IBridgeRuntimeCommandGateway>();
        var launches = provider
            .GetRequiredService<IBridgeManagedRuntimeLaunchCoordinator>();
        var openCodeDirectory = (ActiveOpenCodeEndpointDirectory)provider
            .GetRequiredService<IOpenCodeEndpointDirectory>();
        await openCodeDirectory.StartAsync(CancellationToken.None);
        var openCodeTarget = openCodeDirectory.Register(5_110, cwd);
        Assert.IsTrue(openCodeDirectory.RememberSession(
            openCodeTarget.Port,
            openCodeTarget.Generation,
            "session-opencode"));
        Assert.IsTrue(openCodeDirectory.SetReady(
            openCodeTarget.Port,
            openCodeTarget.Generation,
            ready: true));

        try
        {
            await ingress.DispatchAsync(Launch(
                RuntimeNames.Codex,
                "session-codex",
                cwd,
                "command-codex"));
            await ingress.DispatchAsync(Launch(
                RuntimeNames.OpenCode,
                "session-opencode",
                cwd,
                "command-opencode"));

            Assert.AreEqual("active", preflight.Mode);
            Assert.IsTrue(preflight.Complete);
            CollectionAssert.AreEqual(
                new[]
                {
                    typeof(CodexRuntimeAdapter),
                    typeof(ClaudeCodeRuntimeAdapter),
                    typeof(OpenCodeRuntimeAdapter),
                },
                adapters.Select(adapter => adapter.GetType()).ToArray());
            Assert.IsInstanceOfType<BridgeRuntimeCommandIngress>(ingress);
            Assert.AreEqual(RuntimeNames.Codex, launches.Claim()?.Runtime);
            Assert.AreEqual(RuntimeNames.OpenCode, launches.Claim()?.Runtime);
            Assert.IsNull(launches.Claim());

            var runtimeIngress = provider
                .GetRequiredService<IBridgeRuntimeIngressAssembly>()
                .Validate();
            Assert.AreEqual("active", runtimeIngress.Mode);
            Assert.IsTrue(runtimeIngress.ManagedHookHttpEnabled);
            Assert.IsTrue(runtimeIngress.OpenCodeEventStreamEnabled);
            Assert.IsFalse(Directory.Exists(dataDirectory));
        }
        finally
        {
            await openCodeDirectory.StopAsync(CancellationToken.None);
        }
    }

    private static RuntimeCommandEnvelope Launch(
        string runtime,
        string sessionId,
        string cwd,
        string commandId) => new()
        {
            ProtocolVersion = BridgeProtocolVersion.Current,
            Runtime = runtime,
            Session = new RuntimeSessionReference
            {
                ExternalId = sessionId,
                Cwd = cwd,
            },
            TraceId = $"trace-{commandId}",
            CommandId = commandId,
            CommandType = RuntimeCommandTypes.SessionLaunch,
            CreatedAt = "2026-08-07T00:00:00.000Z",
            Payload = JsonSerializer.SerializeToElement(new
            {
                cwd,
                elevated = false,
            }),
        };
}
