using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public sealed class PassiveOwnerGuardSubsystem(
    ActiveOwnerLeaseObserver observer) :
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth
{
    public string Name => "production-owner";

    public BridgeComponentHealth ComponentHealth { get; private set; } =
        new("production-owner", "starting");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var snapshot = await observer.InspectAsync(cancellationToken);
        ComponentHealth = new(
            Name,
            "passive",
            snapshot.State switch
            {
                ActiveOwnerLeaseState.Live =>
                    $"active-owner-{snapshot.Record!.HostKind}-live",
                ActiveOwnerLeaseState.Stale =>
                    $"active-owner-{snapshot.Record!.HostKind}-stale",
                ActiveOwnerLeaseState.Invalid => "active-owner-lease-invalid",
                _ => "active-owner-lease-missing",
            });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ComponentHealth = new(Name, "starting");
        return Task.CompletedTask;
    }
}
