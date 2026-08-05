using Microsoft.AspNetCore.Server.Kestrel.Core;

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
        builder.Services.AddSingleton<IBridgeHostSubsystem, PassiveOwnerGuardSubsystem>();
        builder.Services.AddHostedService<BridgeInstanceLeaseService>();
        builder.Services.AddHostedService<BridgeRuntimeWorker>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapBridgeControlApi();
        return app;
    }
}
