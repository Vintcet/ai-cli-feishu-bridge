using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Hosts the Feishu event pump without granting production ownership. The passive
/// source completes immediately, so this validates lifecycle wiring without I/O.
/// </summary>
public sealed class BridgeFeishuEventSubsystem(
    FeishuEventPump eventPump,
    BridgeHostOptions options) : IBridgeHostSubsystem, IBridgeHostSubsystemHealth
{
    private bool started;

    public string Name => "feishu-event-pump";

    public BridgeComponentHealth ComponentHealth => started
        ? new(Name, "passive", "event-source-disabled")
        : new(Name, "starting");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Passive)
        {
            throw new InvalidOperationException(
                "The Feishu event pump may only use the no-I/O source while the host is passive.");
        }
        await eventPump.RunAsync(cancellationToken);
        started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        started = false;
        return Task.CompletedTask;
    }
}
