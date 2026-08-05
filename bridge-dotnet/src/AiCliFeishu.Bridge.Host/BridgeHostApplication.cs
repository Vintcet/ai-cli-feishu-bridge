using Microsoft.AspNetCore.Server.Kestrel.Core;
using AiCliFeishu.Bridge.Adapters.Feishu;
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
        builder.Services.AddSingleton<IBridgeRuntimeCommandGateway, BridgeRuntimeCommandGateway>();
        builder.Services.AddSingleton<ReadOnlyNodeStoreShadow>();
        builder.Services.AddSingleton<IBridgeStoreShadow>(services =>
            services.GetRequiredService<ReadOnlyNodeStoreShadow>());
        builder.Services.AddSingleton<IBridgeHostSubsystem, PassiveOwnerGuardSubsystem>();
        builder.Services.AddSingleton<IBridgeHostSubsystem, BridgeBoundarySubsystem>();
        builder.Services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<ReadOnlyNodeStoreShadow>());
        builder.Services.AddHostedService<BridgeInstanceLeaseService>();
        builder.Services.AddHostedService<BridgeRuntimeWorker>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapBridgeControlApi();
        return app;
    }
}
