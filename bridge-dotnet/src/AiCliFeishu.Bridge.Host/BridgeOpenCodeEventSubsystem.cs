using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Owns OpenCode event subscriptions in the Host lifecycle. Passive mode requires
/// an empty endpoint directory, so no SSE request can be opened during shadow runs.
/// </summary>
public sealed class BridgeOpenCodeEventSubsystem(
    IOpenCodeEndpointDirectory endpoints,
    OpenCodeRuntimeEventPump eventPump,
    BridgeHostOptions options) : IBridgeHostSubsystem, IBridgeHostSubsystemHealth
{
    private bool started;

    public string Name => "opencode-event-pump";

    public BridgeComponentHealth ComponentHealth => started
        ? new(Name, "passive", "event-endpoints-disabled")
        : new(Name, "starting");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Passive)
        {
            throw new InvalidOperationException(
                "OpenCode event subscriptions are not enabled for an active C# Host.");
        }
        var readyEndpoints = endpoints.ListReady();
        if (readyEndpoints.Count != 0)
        {
            throw new InvalidOperationException(
                "Passive Host cannot subscribe to OpenCode event endpoints.");
        }
        await Task.WhenAll(readyEndpoints.Select(endpoint =>
            eventPump.RunAsync(endpoint, cancellationToken)));
        started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        started = false;
        return Task.CompletedTask;
    }
}
