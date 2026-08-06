using Microsoft.AspNetCore.Server.Kestrel.Core;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

public static class BridgeHostApplication
{
    public static WebApplication Build(
        BridgeHostOptions options,
        string[]? args = null,
        Action<IServiceCollection>? configureServices = null)
    {
        options = options.Validate();
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(options.ListenAddress, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<BridgeHealthRegistry>();
        builder.Services.AddSingleton<IBridgeInstanceLease, FileBridgeInstanceLease>();
        builder.Services.AddSingleton<IBridgeControlTokenProvider, FileBridgeControlTokenProvider>();
        builder.Services.AddSingleton<BridgeBusinessStateOwner>();
        builder.Services.AddSingleton<IBridgeRuntimeEventHandler>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        builder.Services.AddSingleton<IBridgeFeishuIntentHandler>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        builder.Services.AddSingleton<BridgeRuntimeEventIngress>();
        builder.Services.AddSingleton<IRuntimeEventSink>(services =>
            services.GetRequiredService<BridgeRuntimeEventIngress>());
        builder.Services.AddSingleton<BridgeFeishuIntentIngress>();
        builder.Services.AddSingleton<IFeishuIntentSink>(services =>
            services.GetRequiredService<BridgeFeishuIntentIngress>());
        builder.Services.AddSingleton<BridgeBoundaryCatalog>();
        builder.Services.AddSingleton<RuntimeAdapterRegistry>(services =>
            services.GetRequiredService<BridgeBoundaryCatalog>().BuildRuntimeRegistry());
        builder.Services.AddSingleton<RuntimeCommandDispatcher>();
        builder.Services.AddSingleton<BridgeRuntimeCommandGateway>();
        builder.Services.AddSingleton<BridgeRuntimeCommandIngress>();
        builder.Services.AddSingleton<IBridgeRuntimeCommandGateway>(services =>
            services.GetRequiredService<BridgeRuntimeCommandIngress>());
        builder.Services.AddSingleton<IManagedTerminalDirectory, PassiveManagedTerminalDirectory>();
        builder.Services.AddSingleton<IManagedTerminalTransport, PassiveManagedTerminalTransport>();
        builder.Services.AddSingleton<IManagedRuntimeLifecycle, PassiveManagedRuntimeLifecycle>();
        builder.Services.AddSingleton<IManagedHookResponseSink, PassiveManagedHookResponseSink>();
        builder.Services.AddSingleton<IOpenCodeEndpointDirectory, PassiveOpenCodeEndpointDirectory>();
        builder.Services.AddSingleton<IOpenCodeTransport, PassiveOpenCodeTransport>();
        builder.Services.AddSingleton<IOpenCodeRuntimeLifecycle, PassiveOpenCodeRuntimeLifecycle>();
        builder.Services.AddSingleton<IRuntimeAdapter, CodexRuntimeAdapter>();
        builder.Services.AddSingleton<IRuntimeAdapter, ClaudeCodeRuntimeAdapter>();
        builder.Services.AddSingleton<IRuntimeAdapter, OpenCodeRuntimeAdapter>();
        builder.Services.AddSingleton<ReadOnlyNodeStoreShadow>();
        builder.Services.AddSingleton<IBridgeStoreShadow>(services =>
            services.GetRequiredService<ReadOnlyNodeStoreShadow>());
        builder.Services.AddSingleton<BridgeControlStatusReader>();
        builder.Services.AddSingleton<IBridgeHostSubsystem, PassiveOwnerGuardSubsystem>();
        builder.Services.AddSingleton<IBridgeHostSubsystem, BridgeBoundarySubsystem>();
        builder.Services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<ReadOnlyNodeStoreShadow>());
        builder.Services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        builder.Services.AddHostedService<BridgeInstanceLeaseService>();
        builder.Services.AddHostedService<BridgeRuntimeWorker>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapBridgeControlApi();
        return app;
    }
}
